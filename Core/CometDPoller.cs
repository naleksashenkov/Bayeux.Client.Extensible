// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Interfaces;
using Bayeux.Client.Extensible.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using static Bayeux.Client.Extensible.Core.Constants.CometDConstants;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// A Bayeux long-polling client. One instance owns exactly one CometD session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="IBayeuxPoller"/> for the behavioural contract: background operation, no
    /// automatic retry, single-threaded lifecycle.
    /// </para>
    /// <para>
    /// The poller manages session cookies itself, per request, so that several pollers can share
    /// one <see cref="HttpClient"/> without their sessions colliding. The client's handler must
    /// therefore have <c>UseCookies = false</c>; otherwise both layers manage cookies and sessions
    /// on the same host overwrite one another.
    /// </para>
    /// </remarks>
    public class CometDPoller : IBayeuxPoller
    {
        private CancellationTokenSource _bayeuxCts = new CancellationTokenSource();

        private CancellationToken _token;

        private readonly CookieContainer _cookieContainer = new CookieContainer();

        private readonly ILogger<CometDPoller> _logger;

        private readonly SemaphoreSlim _channelLock = new(1, 1);

        // Events that arrived on a reply sent while _channelLock was held. Held back rather than
        // delivered, because a handler that blocks on a subscribe it starts would wait for a lock its
        // own caller holds. Emptied by ReleaseChannelLock, so it is empty outside a lock scope.
        private readonly ConcurrentQueue<BayeuxResponseMessageModel> _messageQueue = new ();
        
        // True while a message handler - and everything it awaits - is running. Lets DisconnectAsync
        // and ConnectAsync recognise a call made from inside a handler: the loop is waiting for that
        // handler to return, so waiting for the loop from there would wait forever.
        private readonly AsyncLocal<bool> _insideDispatch = new();

        private Task? _bayeuxTask;

        private long _cometdMessageId = 0;

        private readonly HttpClient _client;

        private bool _isMultipleClientsWarningShown;

        private volatile bool _isDestroyed = false;

        /// <summary>Returns the next Bayeux message id. Ids are per-poller and monotonic.</summary>
        private string GetNextId() => Interlocked.Increment(ref _cometdMessageId).ToString();

        /// <inheritdoc/>
        public IAuthProvider AuthProvider { get; }

        /// <inheritdoc/>
        public CometdPollerOptions Options { get; }

        /// <inheritdoc/>
        public string ClientId { get; private set; }

        /// <inheritdoc/>
        public bool IsDestroyed => _isDestroyed;

        /// <summary>
        /// Milliseconds the server last asked the client to wait between <c>/meta/connect</c>
        /// requests, taken from <c>advice.interval</c>. Usually zero, since the server holds the
        /// request open instead of asking the client to wait.
        /// </summary>
        public int? ConnectInterval { get; private set; } = 0;

        /// <inheritdoc/>
        public event EventHandler<OnPollerDisconnectedEventArgs> OnPollerDisconnected;

        /// <inheritdoc/>
        public event EventHandler<OnPollerErrorEventArgs>? OnError;

        /// <summary>Creates a poller for one CometD session.</summary>
        /// <param name="client">
        /// A long-lived client whose handler has <c>UseCookies = false</c>. It may be shared with
        /// other pollers and with the application's own requests; it must not be shared between
        /// different user sessions on the same host. Its <see cref="HttpClient.Timeout"/> must
        /// exceed the server's <c>advice.timeout</c>, which the poller verifies after the
        /// handshake. On .NET Framework, also raise
        /// <c>ServicePointManager.DefaultConnectionLimit</c>: each poller holds one connection open
        /// permanently, and the default limit is two.
        /// </param>
        /// <param name="options">Channels, endpoint path and timeouts.</param>
        /// <param name="authProvider">
        /// Authenticates every request. Use <see cref="NoAuthProvider.Instance"/> for servers that
        /// need none.
        /// </param>
        /// <param name="onPollerDisconnected">
        /// Handler for <see cref="OnPollerDisconnected"/>. Required, because the poller never
        /// retries on its own and this is how a caller learns the session ended.
        /// </param>
        /// <param name="cookie">
        /// Cookies inherited from a session established elsewhere, such as an application login.
        /// A private container is created when omitted. Cookies the server issues later, including
        /// <c>BAYEUX_BROWSER</c>, are captured into it automatically.
        /// </param>
        /// <param name="logger">
        /// Optional. Receives diagnostics that the events do not carry &#8212; session ids, server
        /// advice, reconnect timings. Never receives request or response bodies, which hold
        /// credentials and live data. Defaults to a no-op logger.
        /// </param>
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="client"/> has no base address.</exception>
        public CometDPoller(
            HttpClient client,
            CometdPollerOptions options,
            IAuthProvider authProvider,
            EventHandler<OnPollerDisconnectedEventArgs> onPollerDisconnected,
            CookieContainer? cookie = null,
            ILogger<CometDPoller>? logger = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            if (client.BaseAddress == null)
                throw new ArgumentException("Client base address can not be null.", nameof(client)); 

            Options = options ?? throw new ArgumentNullException(nameof(options));
            AuthProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
            ClientId = string.Empty;

            OnPollerDisconnected = onPollerDisconnected ?? throw new ArgumentNullException(nameof(onPollerDisconnected));

            _cookieContainer = cookie ?? new CookieContainer();
            _logger = logger ?? NullLogger<CometDPoller>.Instance;

            _token = _bayeuxCts.Token;
        }

        /// <summary>
        /// Signals the polling loop to stop and returns immediately.
        /// </summary>
        /// <remarks>
        /// The loop finishes asynchronously, so it may still send <c>/meta/disconnect</c> and raise
        /// <see cref="OnPollerDisconnected"/> after this method returns &#8212; using the
        /// <see cref="HttpClient"/> that was passed in. Use <see cref="DisposeAsync"/> when the
        /// caller needs shutdown to have completed before it continues.
        /// </remarks>
        public void Dispose()
        {
            if (_isDestroyed)
                return;

            _isDestroyed = true;

            _bayeuxCts.Cancel();
        }

        /// <summary>
        /// Stops the polling loop, waits for it to finish, and releases resources.
        /// </summary>
        /// <returns>A task that completes once the loop has stopped.</returns>
        /// <remarks>
        /// Unlike <see cref="Dispose"/>, the disconnect request and
        /// <see cref="OnPollerDisconnected"/> have both completed when this returns.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (_isDestroyed)
                return;

            _isDestroyed = true;

            _bayeuxCts.Cancel();

            var currentTask = _bayeuxTask;

            if (currentTask != null)
            {
                try
                {
                    await currentTask.ConfigureAwait(false);
                }
                catch { }

                if (ReferenceEquals(_bayeuxTask, currentTask))
                    _bayeuxTask = null;
            }

            _bayeuxCts.Dispose();
            _channelLock.Dispose();

            _logger.LogDebug("Poller disposed");
        }

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(CometDPoller));
            
            if (_insideDispatch.Value)
                throw new InvalidOperationException(
                "ConnectAsync cannot be called from a message handler: the poller's loop is waiting for the "
                + "handler to return, so it cannot be restarted from inside it. Call DisconnectAsync in the "
                + "handler, and reconnect from OnPollerDisconnected, which runs after the loop has finished.");

            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);

            try
            {
                await SetUpConnectionAsync(call.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _bayeuxCts.Cancel();

                await SendDisconnectAsync().ConfigureAwait(false);
                throw;
            }

            _bayeuxTask = Task.Run(() => StartDialogueAsync());
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(CometDPoller));

            var currentTask = _bayeuxTask;

            if (currentTask != null)
            {
                _bayeuxCts.Cancel();

                if (_insideDispatch.Value)
                    return;

                if (cancellationToken.CanBeCanceled)
                {
                    await Task.WhenAny(currentTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                try
                {
                    await currentTask.ConfigureAwait(false);
                }
                catch { }

                if (ReferenceEquals(_bayeuxTask, currentTask))
                    _bayeuxTask = null;
            }

            if (_bayeuxCts.IsCancellationRequested)
            {
                _bayeuxCts.Dispose();
                _bayeuxCts = new CancellationTokenSource();
            }

            _token = _bayeuxCts.Token;
        }

        /// <inheritdoc/>
        public async Task SubscribeNewChannelsAsync(IDictionary<string, BayeuxEventHandler> channels, CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(CometDPoller));

            if (string.IsNullOrEmpty(ClientId))
                throw new InvalidOperationException(
                    "No established session. Call ConnectAsync first, or wait: the poller may be "
                    + "re-establishing the session after the server invalidated it.");

            if (channels == null || channels.Count == 0)
                throw new ArgumentException("Channels array is null or empty", nameof(channels));

            var notAdded = new List<string>();
            var added = new List<string>();

            using var call = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            await _channelLock.WaitAsync(call.Token).ConfigureAwait(false);
            
            try
            {
                foreach (var channel in channels)
                {
                    if (!Options.InternalChannels.TryAdd(channel.Key, channel.Value))
                        notAdded.Add(channel.Key);
                    else
                        added.Add(channel.Key);
                }

                if (added.Count == 0)
                    throw new InvalidOperationException($"No channels were added. Channels not added: {string.Join(", ", notAdded)}");

                Dictionary<string, Exception> failedSubscribes;

                try
                {
                    try
                    {
                        failedSubscribes = await SubscribeChannelsAsync(ClientId, added, false, call.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        if (added.Count > 0)
                        {
                            foreach (var item in added)
                            {
                                Options.InternalChannels.TryRemove(item, out _);
                                notAdded.Add(item);
                            }
                        }

                        added.Clear();

                        throw;
                    }

                    if (failedSubscribes.Count > 0)
                    {
                        foreach (var failedSubscribe in failedSubscribes)
                        {
                            Options.InternalChannels.TryRemove(failedSubscribe.Key, out _);

                            notAdded.Add(failedSubscribe.Key);
                            added.Remove(failedSubscribe.Key);
                        }

                        throw new BayeuxSubscriptionException(failedSubscribes, added);
                    }
                }
                finally
                {
                    _logger.LogInformation(
                        "Subscribed {Added}\n Skipped {Skipped}\n Session {ClientId}",
                        string.Join(", ", added), string.Join(", ", notAdded), ClientId);
                }
            }
            finally
            {
                await ReleaseChannelLockAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public async Task UnsubscribeChannelsAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(CometDPoller));

            if (string.IsNullOrEmpty(ClientId))
                throw new InvalidOperationException(
                    "No established session. Call ConnectAsync first, or wait: the poller may be "
                    + "re-establishing the session after the server invalidated it.");

            var requested = channels as IReadOnlyList<string> ?? channels?.ToList();

            if (requested is not { Count: > 0 })
                throw new ArgumentException("Channels array is null or empty", nameof(channels));

            var notRemoved = new List<string>();
            var removed = new Dictionary<string, BayeuxEventHandler>();
            
            using var call = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            await _channelLock.WaitAsync(call.Token).ConfigureAwait(false);

            try
            {
                foreach (var channel in requested)
                {
                    if (!Options.InternalChannels.TryRemove(channel, out var handler))
                        notRemoved.Add(channel);
                    else
                        removed.Add(channel, handler);
                }

                if (removed.Count == 0)
                    throw new InvalidOperationException($"No channels were removed. Channels not removed: {string.Join(", ", notRemoved)}");

                Dictionary<string, Exception> failedUnsubscribes;

                try
                {
                    try
                    {
                        failedUnsubscribes = await SendUnsubscribeChannelsAsync(ClientId, removed.Keys, call.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        if (removed.Count > 0)
                        {
                            foreach (var item in removed)
                            {
                                Options.InternalChannels.TryAdd(item.Key, item.Value);
                                notRemoved.Add(item.Key);
                            }
                        }

                        removed.Clear();

                        throw;
                    }

                    if (failedUnsubscribes.Count > 0)
                    {
                        foreach (var failedUnsubscribe in failedUnsubscribes)
                        {
                            if (removed.TryGetValue(failedUnsubscribe.Key, out var handler))
                            {
                                Options.InternalChannels.TryAdd(failedUnsubscribe.Key, handler);
                                removed.Remove(failedUnsubscribe.Key);
                            }

                            notRemoved.Add(failedUnsubscribe.Key);
                        }

                        throw new BayeuxSubscriptionException(failedUnsubscribes, removed.Keys.ToList());
                    }
                }
                finally
                {
                    _logger.LogInformation(
                        "Not removed {NotRemoved}\n Removed {Removed}\n Session {ClientId}",
                        string.Join(", ", notRemoved), string.Join(", ", removed.Keys), ClientId);
                }
            }
            finally
            {
                await ReleaseChannelLockAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Establishes a session: handshake, then subscribe to every configured channel.
        /// </summary>
        /// <remarks>
        /// Used both for the first connection and for recovery after the server invalidates the
        /// session. Handshake and subscribe stay together because a new client id means a new
        /// session, and the previous subscriptions no longer exist on the server.
        /// </remarks>
        private async Task SetUpConnectionAsync(CancellationToken cancellationToken)
        {
            await _channelLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _isMultipleClientsWarningShown = false;
                ClientId = string.Empty;
                var clientId = await HandshakeAsync(cancellationToken).ConfigureAwait(false);

                if (Options.Channels.Count > 0)
                    await SubscribeChannelsAsync(clientId, Options.Channels.Keys, true, cancellationToken).ConfigureAwait(false);

                ClientId = clientId;
            }
            finally
            {
                await ReleaseChannelLockAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The polling loop. Runs until the caller cancels, the server ends the session, or an
        /// error occurs; then reports the outcome through the disconnected event.
        /// </summary>
        /// <remarks>
        /// Clears <c>_bayeuxTask</c> before raising the event, so a consumer may call
        /// <see cref="ConnectAsync"/> from the handler without waiting on the task it is running in.
        /// </remarks>
        private async Task StartDialogueAsync()
        {
            var disconnectReason = DisconnectReason.TokenCancellation;
            Exception? failure = null;

            try
            {
                while (!_token.IsCancellationRequested)
                {
                    BayeuxResponseAdviceModel? advice = null;

                    var verdict = ConnectResult.Continue;
                    var messages = await PostCometdAsync([new ConnectRequestModel(ClientId, GetNextId())], _token).ConfigureAwait(false);

                    foreach (var message in messages)
                    {
                        if (message.Channel == MetaChannels.Connect)
                        {
                            advice = message.Advice;
                            if (advice != null)
                            {
                                ConnectInterval = advice.Interval ?? ConnectInterval;
                                verdict = VerdictConnectResult(advice);

                                if (advice.IsMultipleClients == true && _isMultipleClientsWarningShown == false)
                                {
                                    _isMultipleClientsWarningShown = true;
                                    RaiseOnError(ErrorSource.Connect, "Multiple clients are sharing BAYEUX_BROWSER cookie now", false);
                                }
                                else if (advice.IsMultipleClients != true && _isMultipleClientsWarningShown == true)
                                    _isMultipleClientsWarningShown = false;
                            }
                            else if (message.IsSuccessful == false)
                                verdict = ConnectResult.Rehandshake;
                                    
                        }
                        else if (message.Channel == MetaChannels.Disconnect)
                            verdict = ConnectResult.Stop;
                    }
                    
                    if (verdict == ConnectResult.Stop)
                    {
                        disconnectReason = DisconnectReason.ServerRequirement;
                        break;
                    }
                    else if (verdict == ConnectResult.Rehandshake)
                    {
                        _logger.LogInformation(
                            "Session {ClientId} invalidated by the server (advice: {Reconnect}); re-establishing",
                            ClientId, advice?.Reconnect ?? "none");

                        await SetUpConnectionAsync(_token).ConfigureAwait(false);
                    }
                    else if (verdict == ConnectResult.Continue)
                        await Task.Delay(ConnectInterval ?? 0, _token).ConfigureAwait(false);
                }
                
                _token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                disconnectReason = DisconnectReason.Failed;
                failure = ex;
            }
            finally
            {
                _bayeuxTask = null;

                if (!_bayeuxCts.IsCancellationRequested)
                    _bayeuxCts.Cancel();

                await TryDisconnectAsync(disconnectReason, failure).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Checks that the HTTP client's timeout can outlast a held <c>/meta/connect</c>.
        /// </summary>
        /// <param name="serverTimeoutMs">The server's <c>advice.timeout</c>, or <c>null</c> if absent.</param>
        /// <exception cref="InvalidOperationException">The client would time out before the server replies.</exception>
        /// <remarks>
        /// Without this the failure looks like a recurring network fault: every long poll aborts on
        /// a healthy connection once the client's timeout elapses.
        /// </remarks>
        private void ValidateClientTimeout(int? serverTimeoutMs)
        {
            if (_client.Timeout == Timeout.InfiniteTimeSpan || serverTimeoutMs == null)
                return;

            var requiredSpan = TimeSpan.FromMilliseconds(serverTimeoutMs.Value) + TimeSpan.FromSeconds(5);

            if (_client.Timeout < requiredSpan)
                throw RaiseOnError(
                    ErrorSource.Configuration,
                    $"HttpClient timeout is {_client.Timeout.TotalSeconds:0.#} seconds " +
                    $"that is less than server recommended {requiredSpan.TotalSeconds:0.#} seconds " +
                    "Please raise it to recommended value at least",
                    true);
        }

        /// <summary>Maps the server's <c>advice.reconnect</c> to what the loop should do next.</summary>
        /// <param name="advice">Advice from a <c>/meta/connect</c> reply.</param>
        /// <returns>Continue for <c>retry</c> or an unknown value, which is the protocol default.</returns>
        private ConnectResult VerdictConnectResult(BayeuxResponseAdviceModel advice)
        {
            var reconnect = advice.Reconnect;
            if (reconnect == Reconnect.None)
                return ConnectResult.Stop;
            else if (reconnect == Reconnect.Handshake)
                return ConnectResult.Rehandshake;
            
            return ConnectResult.Continue;
        }

        /// <summary>Delivers one broadcast message to every subscription that matches its channel.</summary>
        /// <param name="message">A message on a non-meta channel.</param>
        /// <remarks>
        /// <para>
        /// Every registered pattern is tested, not just the exact name, and each match is invoked.
        /// A message on <c>/a/b</c> therefore reaches handlers registered for both <c>/a/**</c> and
        /// <c>/a/b</c> &#8212; CometD's own behaviour, and the reason this is a loop rather than a
        /// dictionary lookup.
        /// </para>
        /// <para>
        /// Handler exceptions are reported through the error event and swallowed: a defect in a
        /// consumer's handler must not stop the poller, and one failing handler must not deny the
        /// message to the others.
        /// </para>
        /// </remarks>
        private async Task HandleChannelMessageAsync(BayeuxResponseMessageModel message)
        {
            if (message.Channel is not { Length: > 0 } channel)
                return;

            var ext = message.Ext is null ? null : new ReadOnlyDictionary<string, JsonElement>(message.Ext);
            var @event = new BayeuxEvent(channel, message.Data, ext);
            var capturedToken = _token;

            // Never reset, and it does not need to be. A value set on an AsyncLocal inside an async
            // method flows into everything the method calls or awaits - the handlers - but not back
            // out to its caller: once this method returns, the caller sees what it saw before. The
            // flag is therefore true for the handlers and false again for the polling loop,
            // OnPollerDisconnected included. It also flows into a Task.Run a handler starts, so a
            // fire-and-forget ConnectAsync from a handler is refused as well - rightly, since the
            // DisconnectAsync inside it would not wait for the old loop to finish.
            _insideDispatch.Value = true;

            foreach (var subscription in Options.Channels)
            {
                if (!DoesChannelMatch(channel, subscription.Key))
                    continue;

                try
                {
                    await subscription.Value(@event, capturedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (capturedToken.IsCancellationRequested)
                { }
                catch (Exception ex)
                {
                    RaiseOnError(ErrorSource.Handler, ex, false);
                }
            }
        }

        /// <summary>Tests a concrete channel name against a Bayeux subscription pattern.</summary>
        /// <param name="channel">
        /// The channel a message arrived on. Always a concrete name &#8212; a server never sends a
        /// wildcard in the <c>channel</c> field.
        /// </param>
        /// <param name="pattern">
        /// A pattern the caller subscribed to: an exact name, <c>/a/*</c> for one further segment,
        /// or <c>/a/**</c> for any depth below.
        /// </param>
        /// <returns><c>true</c> when the message belongs to that subscription.</returns>
        /// <remarks>
        /// Comparisons are ordinal: channel names are protocol identifiers, not text for a user.
        /// <c>/**</c> is tested before <c>/*</c> so the order does not depend on how the patterns
        /// happen to end.
        /// </remarks>
        private static bool DoesChannelMatch(string channel, string pattern)
        {
            if (string.Equals(channel, pattern, StringComparison.Ordinal))
                return true;

            if (pattern.EndsWith("/**", StringComparison.Ordinal))
                return channel.StartsWith(pattern.Substring(0, pattern.Length - 2), StringComparison.Ordinal);

            if (pattern.EndsWith("/*", StringComparison.Ordinal))
                return channel.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal)
                    && channel.LastIndexOf('/') == pattern.LastIndexOf('/');

            return false;
        }

        /// <summary>
        /// Ends the session and raises the disconnected event. Called once, as the loop exits.
        /// </summary>
        /// <param name="reason">Why the loop stopped.</param>
        /// <param name="failure">The error that stopped it, if any.</param>
        /// <remarks>
        /// No disconnect request is sent for <see cref="DisconnectReason.ServerRequirement"/>: the
        /// server has already closed the session, so the request would only earn another error.
        /// </remarks>
        private async Task TryDisconnectAsync(DisconnectReason reason, Exception? failure = null)
        {
            Exception? disconnectException = null;

            if (reason != DisconnectReason.ServerRequirement)
                disconnectException = await SendDisconnectAsync().ConfigureAwait(false);

            RaiseOnDisconnected(OnPollerDisconnectedEventArgs.Create(reason, failure, disconnectException));
        }

        /// <summary>
        /// Sends <c>/meta/disconnect</c> on a best-effort basis and clears the client id.
        /// </summary>
        /// <returns>The failure, or <c>null</c> if the server acknowledged it or there was no session.</returns>
        /// <remarks>
        /// Returns the error rather than throwing, because both callers want to carry on: the loop
        /// puts it in the event, and a failed <see cref="ConnectAsync"/> ignores it while releasing
        /// a half-created session. Uses its own timeout, so a cancelled session token cannot stop
        /// the goodbye from being sent.
        /// </remarks>
        private async Task<Exception?> SendDisconnectAsync()
        {
            if (string.IsNullOrEmpty(ClientId))
                return null;

            try
            {
                using (var cts = new CancellationTokenSource(Options.DisconnectTimeout))
                {
                    var messages = await PostCometdAsync([new DisconnectRequestModel(ClientId, GetNextId())], cts.Token).ConfigureAwait(false);

                    var message = messages.FirstOrDefault(message => message.Channel == MetaChannels.Disconnect);

                    if (message?.IsSuccessful == false)
                        return RaiseOnError(ErrorSource.Disconnect, string.IsNullOrEmpty(message.Error)
                            ? $"{MetaChannels.Disconnect} fault without message"
                            : message.Error!,
                            false);

                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                ClientId = string.Empty;
            }
        }

        /// <summary>Sends one <c>/meta/subscribe</c> request carrying every given channel.</summary>
        /// <param name="clientId">The session id.</param>
        /// <param name="subscriptions">The channel names, sent as a single batch.</param>
        /// <param name="isFatal">
        /// Whether a rejection ends the poller. True during session setup, where a channel the
        /// caller asked for is missing and the session is not what was requested; false for
        /// channels added at runtime, where the failure goes back to the caller and polling
        /// continues on the channels that were accepted.
        /// </param>
        /// <param name="cancellationToken">
        /// Cancels the request. Whoever calls decides which token that is: a runtime subscribe passes
        /// the call's linked token, session setup the session's own.
        /// </param>
        /// <returns>
        /// The rejected channels and the reason for each. Empty when the server accepted them all.
        /// </returns>
        /// <exception cref="BayeuxSubscriptionException">
        /// <paramref name="isFatal"/> is set and at least one channel was rejected. The same type is
        /// used at setup and at runtime, so a caller has one thing to catch either way.
        /// </exception>
        /// <remarks>
        /// A rejection is reported per channel rather than for the batch: the server answers each
        /// subscription separately, and accepting nine of ten channels is not the same failure as
        /// losing the connection.
        /// </remarks>
        private async Task<Dictionary<string, Exception>> SubscribeChannelsAsync(
            string clientId, IEnumerable<string> subscriptions, 
            bool isFatal,
            CancellationToken cancellationToken)
        {
            var list = subscriptions as IReadOnlyList<string> ?? subscriptions.ToList();
            var requestModels = new SubscribeRequestModel[list.Count];
            for (var i = 0; i < list.Count; i++)
                requestModels[i] = new SubscribeRequestModel(clientId, list[i], GetNextId());

            var messages = await PostCometdAsync(requestModels, cancellationToken, true).ConfigureAwait(false);

            var subscribeMessages = messages.Where(message => message.Channel == MetaChannels.Subscribe);

            var failedSubscribes = new Dictionary<string, Exception>();

            
            var index = 0;

            foreach (var message in subscribeMessages)
            {
                if (message.IsSuccessful == false)
                {
                    failedSubscribes[message.Subscription ?? $"unknown-{index++}"] =
                        RaiseOnError(ErrorSource.Subscribe, string.IsNullOrEmpty(message.Error)
                            ? $"{MetaChannels.Subscribe} fault without message"
                            : message.Error!,
                            isFatal);
                }
            }

            if (isFatal && failedSubscribes.Count > 0)
                throw new BayeuxSubscriptionException(
                    failedSubscribes,
                    list.Where(channel => !failedSubscribes.ContainsKey(channel)).ToList());

            return failedSubscribes;
        }

        /// <summary>Sends one <c>/meta/unsubscribe</c> request carrying every given channel.</summary>
        /// <param name="clientId">The session id.</param>
        /// <param name="subscriptions">The channel names, sent as a single batch.</param>
        /// <param name="cancellationToken">Cancels the request: the unsubscribe call's linked token.</param>
        /// <returns>
        /// The rejected channels and the reason for each. Empty when the server accepted them all.
        /// </returns>
        /// <remarks>
        /// A rejection is never fatal here: the session is healthy, only the request was refused,
        /// and the caller decides what to do about a channel that is still delivering.
        /// </remarks>
        private async Task<Dictionary<string, Exception>> SendUnsubscribeChannelsAsync(
            string clientId, 
            IEnumerable<string> subscriptions,
            CancellationToken cancellationToken)
        {
            var list = subscriptions as IReadOnlyList<string> ?? subscriptions.ToList();
            var requestModels = new UnsubscribeRequestModel[list.Count];
            for (var i = 0; i < list.Count; i++)
                requestModels[i] = new UnsubscribeRequestModel(clientId, list[i], GetNextId());

            var messages = await PostCometdAsync(requestModels, cancellationToken, true).ConfigureAwait(false);

            var unsubscribeMessages = messages.Where(message => message.Channel == MetaChannels.Unsubscribe);

            var failedUnsubscribes = new Dictionary<string, Exception>();

            var index = 0;

            foreach (var message in unsubscribeMessages)
            {
                if (message.IsSuccessful == false)
                {
                    failedUnsubscribes[message.Subscription ?? $"unknown-{index++}"] =
                        RaiseOnError(ErrorSource.Unsubscribe, string.IsNullOrEmpty(message.Error)
                            ? $"{MetaChannels.Unsubscribe} fault without message"
                            : message.Error!,
                            false);
                }
            }

            return failedUnsubscribes;
        }

        /// <summary>Performs <c>/meta/handshake</c> and returns the session id it issues.</summary>
        /// <returns>The new client id.</returns>
        /// <exception cref="InvalidOperationException">
        /// The reply is missing, the handshake was rejected, or it succeeded without a client id.
        /// </exception>
        /// <remarks>
        /// <c>authSuccessful</c> is optional in the specification and most servers omit it, so only
        /// an explicit <c>false</c> counts as an authentication failure.
        /// </remarks>
        private async Task<string> HandshakeAsync(CancellationToken cancellationToken)
        {
            var messages = await PostCometdAsync([new HandshakeRequestModel(GetNextId())], cancellationToken, true).ConfigureAwait(false);

            var message = messages.FirstOrDefault(message => message.Channel == MetaChannels.Handshake);

            if (message == null)
                throw RaiseOnError(ErrorSource.Handshake, "Message in channel /meta/handshake was null.", true);

            if (message.IsSuccessful != true)
                throw RaiseOnError(ErrorSource.Handshake, string.IsNullOrEmpty(message.Error)
                    ? $"{MetaChannels.Handshake} fault without message."
                    : message.Error!,
                    true);

            ValidateClientTimeout(message.Advice?.Timeout);

            if (!string.IsNullOrEmpty(message.ClientId))
            {
                _logger.LogInformation("Handshake succeeded with session {ClientId}", message.ClientId);

                return message.ClientId!;
            }

            throw RaiseOnError(ErrorSource.Handshake, "Failed to receive a valid clientId.", true);
        }

        /// <summary>Sends one Bayeux request and parses the reply.</summary>
        /// <param name="requestModel">
        /// The messages to send as one request. They must share a channel, since the first one names
        /// the endpoint for all of them.
        /// </param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <param name="deferred">
        /// <c>true</c> when the caller holds <c>_channelLock</c>. Events in the reply are then queued
        /// for <see cref="ReleaseChannelLockAsync"/> instead of being delivered now. Pass it from every
        /// call made under the lock: a handler run here could otherwise block on a subscribe that
        /// waits for this very lock.
        /// </param>
        /// <returns>The messages in the reply, possibly empty, never null.</returns>
        /// <remarks>
        /// <para>
        /// Cookies are captured before the status is checked, because an error response may still
        /// rotate the session cookie. Transport failures are reported through the error event and
        /// rethrown; whether they are fatal depends on which message was sent.
        /// </para>
        /// <para>
        /// Events in the reply are handled here rather than by each caller, so that no response path
        /// can drop them. Queued events skip <c>Task.Run</c> on purpose: a cancelled token would
        /// stop the delegate from ever running, and events already received would be lost.
        /// </para>
        /// </remarks>
        private async Task<BayeuxResponseMessageModel[]> PostCometdAsync(
            BaseLongPollingRequestModel[] requestModel, 
            CancellationToken cancellationToken,
            bool deferred = false)
        {
            if (_client.BaseAddress == null)
                throw new ArgumentException("Base address is not set");

            if (requestModel == null || requestModel.Length == 0)
                throw new ArgumentException("Request model is null or empty");

            var endpoint = requestModel.First().Endpoint;

            var uri = new Uri(_client.BaseAddress, $"{Options.CometdPath}/{endpoint}");

            await ProcessOutgoingExtAsync(requestModel, cancellationToken).ConfigureAwait(false);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
                {
                    await AuthProvider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
                        
                    ApplyCookies(request, uri);

                    request.Content = new StringContent(BaseLongPollingRequestModel.FormLongPollingRequestJson(requestModel), Encoding.UTF8, "application/json");

                    using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                    {
                        SaveCookies(response, uri);

                        response.EnsureSuccessStatusCode();
                        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (string.IsNullOrEmpty(responseText))
                            return [];

                        var messages = JsonSerializer.Deserialize<BayeuxResponseMessageModel[]>(responseText) ?? [];

                        ProcessIncomingExt(messages);

                        if (deferred)
                            await ProcessMessagesAsync(messages, deferred).ConfigureAwait(false);
                        else
                            await Task.Run(() => ProcessMessagesAsync(messages, deferred), cancellationToken).ConfigureAwait(false);

                        return messages;
                    }
                }
            }
            catch (Exception ex)
            {   
                if(!requestModel.Any(model => model is SubscribeRequestModel) && 
                    !requestModel.Any(model => model is DisconnectRequestModel) &&
                    !requestModel.Any(model => model is UnsubscribeRequestModel))
                    RaiseOnError(
                        ErrorSource.Http, 
                        ex, 
                        requestModel.Any(model => model is ConnectRequestModel) 
                        || requestModel.Any(model => model is HandshakeRequestModel));
                throw;
            }
        }

        /// <summary>Delivers or queues every event in a response.</summary>
        /// <param name="messages">The messages the server sent back.</param>
        /// <param name="deferred">
        /// Queue the events for <see cref="ReleaseChannelLockAsync"/> instead of running handlers now.
        /// </param>
        /// <remarks>
        /// Runs for every response, not only <c>/meta/connect</c>. CometD flushes the session queue
        /// onto whatever request arrives first, so a subscribe or unsubscribe reply can carry events
        /// ahead of the reply itself; filtering those out would lose them silently.
        /// </remarks>
        private async Task ProcessMessagesAsync(BayeuxResponseMessageModel[] messages, bool deferred)
        {
            foreach (var message in messages)
            {   
                if (message.Channel is { } channel && !channel.StartsWith(MetaChannels.Meta, StringComparison.Ordinal))
                {
                    if (deferred)
                        _messageQueue.Enqueue(message);
                    else
                        await HandleChannelMessageAsync(message).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Attaches this poller's cookies to an outgoing request.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="uri">The target, used to select cookies by domain and path.</param>
        /// <remarks>
        /// Done per request rather than by the HTTP handler so that pollers sharing one client keep
        /// separate sessions. This is why the handler must have <c>UseCookies = false</c>.
        /// </remarks>
        private void ApplyCookies(HttpRequestMessage request, Uri uri)
        {
            var cookieHeader = _cookieContainer.GetCookieHeader(uri);

            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        /// <summary>Captures cookies the server set, including CometD's <c>BAYEUX_BROWSER</c>.</summary>
        /// <param name="response">The response to read.</param>
        /// <param name="uri">The request target the cookies belong to.</param>
        private void SaveCookies(HttpResponseMessage response, Uri uri)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
                return;
            
            foreach (var cookie in cookies)
            {
                _cookieContainer.SetCookies(uri, cookie);
            }
        }

        /// <summary>Raises the disconnected event, swallowing anything the handler throws.</summary>
        /// <param name="args">The outcome to report.</param>
        /// <remarks>
        /// The handler runs while the loop is unwinding, so an exception escaping it would replace
        /// the real cause of the shutdown.
        /// </remarks>
        private void RaiseOnDisconnected(OnPollerDisconnectedEventArgs args)
        {
            try
            {
                OnPollerDisconnected?.Invoke(this, args);
            }
            catch { }
        }

        /// <summary>Creates an exception, reports it through the error event, and returns it.</summary>
        /// <param name="source">Which operation failed.</param>
        /// <param name="message">The failure description.</param>
        /// <param name="isFatal">Whether the poller stops because of it.</param>
        /// <returns>The exception, for the caller to throw.</returns>
        /// <remarks>
        /// Reporting happens where the exception is created, never in a catch that only passes one
        /// through, so a single failure is announced exactly once.
        /// </remarks>
        private Exception RaiseOnError(ErrorSource source, string message, bool isFatal)
        {
            var exception = new InvalidOperationException(message);

            try
            {
                OnError?.Invoke(this, new OnPollerErrorEventArgs(exception, source, isFatal));
            }
            catch { }

            return exception;
        }

        /// <summary>Reports an existing exception through the error event and returns it.</summary>
        /// <param name="source">Which operation failed.</param>
        /// <param name="exception">The failure.</param>
        /// <param name="isFatal">Whether the poller stops because of it.</param>
        /// <returns>The same exception, for the caller to throw or return.</returns>
        private Exception RaiseOnError(ErrorSource source, Exception exception, bool isFatal = false)
        {
            try
            {
                OnError?.Invoke(this, new OnPollerErrorEventArgs(exception, source, isFatal));
            }
            catch { }

            return exception;
        }

        /// <summary>
        /// Releases <c>_channelLock</c>, then delivers the events that arrived while it was held.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The order is the whole point. <c>SemaphoreSlim</c> is not reentrant, so a handler that
        /// blocks on a subscribe it starts - <c>.GetAwaiter().GetResult()</c> from the synchronous
        /// handler signature - must run after the release. Swap these two steps and the deadlock is
        /// back.
        /// </para>
        /// <para>
        /// Called from <c>finally</c>, so events are delivered even when the operation failed: the
        /// server did send them. It must not throw from there, or its exception would replace the one
        /// already propagating - <see cref="HandleChannelMessageAsync"/> swallowing handler exceptions
        /// is what guarantees that.
        /// </para>
        /// </remarks>
        private async Task ReleaseChannelLockAsync()
        {
            _channelLock.Release();

            while (_messageQueue.TryDequeue(out var message))
                await HandleChannelMessageAsync(message).ConfigureAwait(false);
        }

        /// <summary>Lets every extension write into every outgoing message, before serialisation.</summary>
        /// <param name="requestModel">The messages about to be sent as one request.</param>
        /// <param name="cancellationToken">
        /// The token of the operation sending these messages, passed on to every extension.
        /// </param>
        /// <remarks>
        /// Runs before the request is built, outside the transport's try block, so an extension failure
        /// is reported as <see cref="ErrorSource.Ext"/> - not mistaken for an HTTP failure - and
        /// then fails the operation: a message that could not be prepared must not go out.
        /// </remarks>
        private async Task ProcessOutgoingExtAsync(BaseLongPollingRequestModel[] requestModel, CancellationToken cancellationToken)
        {
            foreach (var request in requestModel)
            {
                foreach (var extension in Options.Extensions)
                {
                    try
                    {
                        await extension.OutgoingAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw RaiseOnError(ErrorSource.Ext, ex, request is ConnectRequestModel || request is HandshakeRequestModel);
                    }
                }
            }
        }

        /// <summary>Lets every extension read every incoming message, before anything else does.</summary>
        /// <param name="messages">The messages the server sent back.</param>
        /// <remarks>
        /// A failing extension is reported and skipped, never rethrown: like a failing handler, one
        /// broken extension must not stop the poller or deny the message to the rest.
        /// </remarks>
        private void ProcessIncomingExt(BayeuxResponseMessageModel[] messages)
        {
            foreach (var message in messages)
            {
                foreach (var extension in Options.Extensions)
                {
                    try
                    {
                        extension.Incoming(message);
                    }
                    catch (Exception ex)
                    {
                        RaiseOnError(ErrorSource.Ext, ex, false);
                    }
                }
            }
        }
    }
}