// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Authentication;
using System.Net;
using System.Text;
using System.Text.Json;
using static Bayeux.Client.Extensible.Core.BayeuxConstants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// A Bayeux client. One instance owns exactly one Bayeux session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="IBayeuxClient"/> for the behavioural contract: background operation, no
    /// automatic retry, single-threaded lifecycle.
    /// </para>
    /// <para>
    /// The client manages session cookies itself, per request, so that several Bayeux clients can
    /// share one <see cref="HttpClient"/> without their sessions colliding. The HttpClient's handler must
    /// therefore have <c>UseCookies = false</c>; otherwise both layers manage cookies and sessions
    /// on the same host overwrite one another.
    /// </para>
    /// </remarks>
    public sealed class BayeuxClient : IBayeuxClient
    {
        private CancellationTokenSource _bayeuxCts = new CancellationTokenSource();

        private CancellationToken _token;

        // Jitter for reconnect delays. Drawn from only by the connect loop, and there is one loop at
        // a time, so the instance is never shared between threads. Kept per client rather than in
        // ReconnectOptions, because one options object may serve several clients.
        private readonly Random _random = new Random();

        private readonly ILogger<BayeuxClient> _logger;

        private readonly SemaphoreSlim _channelLock = new(1, 1);

        // Events that arrived on a reply sent while _channelLock was held. Held back rather than
        // delivered, because a handler that blocks on a subscribe it starts would wait for a lock its
        // own caller holds. Emptied by ReleaseChannelLock, so it is empty outside a lock scope.
        private readonly ConcurrentQueue<BayeuxResponseMessageModel> _messageQueue = new ();
        
        // True while a message handler - and everything it awaits - is running. Lets DisconnectAsync
        // and ConnectAsync recognise a call made from inside a handler: the loop is waiting for that
        // handler to return, so waiting for the loop from there would wait forever.
        private readonly AsyncLocal<bool> _insideDispatch = new();

        // The live subscriptions: what is resubscribed after a handshake and what events are
        // dispatched to. Copied from the options at construction and owned by this client alone,
        // so clients sharing one options object - as a DI container shares a singleton - never
        // see each other's subscribe and unsubscribe. Written only under _channelLock; read by
        // dispatch without it, which a ConcurrentDictionary allows.
        private readonly ConcurrentDictionary<string, BayeuxEventHandler> _channels;

        private Task? _bayeuxTask;

        private long _cometdMessageId = 0;

        // BayeuxClientState as an int, because Interlocked works on ints. Changed by whichever thread makes
        // the transition - the caller's in ConnectAsync, the loop's afterwards - and only through
        // ChangeState.
        private int _stateCode = (int)BayeuxClientState.Disconnected;

        private readonly IBayeuxTransport _transport;

        private bool _isMultipleClientsWarningShown;

        private volatile bool _isDestroyed = false;

        // True for the whole run of the connect loop. Tells a failure inside the loop, which a
        // reconnect policy may retry, from one in ConnectAsync, which is never retried. The two do
        // not overlap: ConnectAsync waits for the previous loop before it sets anything up.
        private volatile bool _isLoopRunning;

        /// <summary>
        /// Whether a failed connect or handshake stops the client, which is what
        /// <see cref="BayeuxErrorEventArgs.IsFatal"/> reports: always during
        /// <see cref="ConnectAsync"/>, which never retries; inside the loop only when there is no
        /// reconnect policy.
        /// </summary>
        private bool FailureEndsConnection => !_isLoopRunning || Options.ReconnectOptions is null;

        /// <summary>Returns the next Bayeux message id. Ids are per-client and monotonic.</summary>
        private string GetNextId() => Interlocked.Increment(ref _cometdMessageId).ToString();

        /// <inheritdoc/>
        public IAuthProvider AuthProvider { get; }

        /// <inheritdoc/>
        public BayeuxClientOptions Options { get; }

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
        public event EventHandler<BayeuxDisconnectedEventArgs> OnDisconnected;

        /// <inheritdoc/>
        public event EventHandler<BayeuxErrorEventArgs>? OnError;

        /// <inheritdoc/>
        public event EventHandler<BayeuxStateChangedEventArgs>? OnStateChanged;

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, BayeuxEventHandler> Channels => _channels;

        /// <inheritdoc/>
        public BayeuxClientState State => (BayeuxClientState)Volatile.Read(ref _stateCode);

        /// <summary>Creates a client for one Bayeux session.</summary>
        /// <param name="httpClient">
        /// A long-lived <see cref="HttpClient"/> whose handler has <c>UseCookies = false</c>. It may be
        /// shared with other Bayeux clients and with the application's own requests; it must not be
        /// shared between different user sessions on the same host. With long-polling its
        /// <see cref="HttpClient.Timeout"/> must exceed the server's <c>advice.timeout</c>, which is
        /// verified after the handshake. On .NET Framework, also raise
        /// <c>ServicePointManager.DefaultConnectionLimit</c>: each Bayeux client holds one connection
        /// open permanently, and the default limit is two.
        /// </param>
        /// <param name="options">Channels, endpoint path and timeouts.</param>
        /// <param name="authProvider">
        /// Authenticates every request. Use <see cref="NoAuthProvider.Instance"/> for servers that
        /// need none.
        /// </param>
        /// <param name="onDisconnected">
        /// Handler for <see cref="OnDisconnected"/>. Required, because without
        /// <see cref="BayeuxClientOptions.ReconnectOptions"/> the client does not retry on its own,
        /// and this is how a caller learns the session ended.
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
        /// <exception cref="ArgumentException"><paramref name="httpClient"/> has no base address.</exception>
        public BayeuxClient(
            HttpClient httpClient,
            BayeuxClientOptions options,
            IAuthProvider authProvider,
            EventHandler<BayeuxDisconnectedEventArgs> onDisconnected,
            CookieContainer? cookie = null,
            ILogger<BayeuxClient>? logger = null)
        {
            _ = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            if (httpClient.BaseAddress == null)
                throw new ArgumentException("The HttpClient has no base address.", nameof(httpClient));

            Options = options ?? throw new ArgumentNullException(nameof(options));
            AuthProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
            ClientId = string.Empty;

            _channels = new ConcurrentDictionary<string, BayeuxEventHandler>(options.Channels);

            OnDisconnected = onDisconnected ?? throw new ArgumentNullException(nameof(onDisconnected));
            
            _logger = logger ?? NullLogger<BayeuxClient>.Instance;
            _transport = Options.TransportType switch
            {
                BayeuxTransportType.LongPolling => new LongPollingTransport(httpClient, authProvider, options.Path, cookie ?? new CookieContainer()),
                BayeuxTransportType.WebSocket => throw new NotImplementedException("WebSocket transport is not implemented yet."),
                _ => throw new ArgumentOutOfRangeException(nameof(options.TransportType), options.TransportType, null)
            };
            _token = _bayeuxCts.Token;
        }

        /// <summary>
        /// Signals the connect loop to stop and returns immediately.
        /// </summary>
        /// <remarks>
        /// The loop finishes asynchronously, so it may still send <c>/meta/disconnect</c> and raise
        /// <see cref="OnDisconnected"/> after this method returns &#8212; using the
        /// <see cref="HttpClient"/> that was passed in. Use <see cref="DisposeAsync"/> when the
        /// caller needs shutdown to have completed before it continues.
        /// </remarks>
        public void Dispose()
        {
            if (_isDestroyed)
                return;

            _bayeuxCts.Cancel();

            _isDestroyed = true;
            _transport.DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Stops the connect loop, waits for it to finish, and releases resources.
        /// </summary>
        /// <returns>A task that completes once the loop has stopped.</returns>
        /// <remarks>
        /// Unlike <see cref="Dispose"/>, the disconnect request and
        /// <see cref="OnDisconnected"/> have both completed when this returns.
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

            await _transport.DisposeAsync().ConfigureAwait(false);
            _bayeuxCts.Dispose();
            _channelLock.Dispose();

            _logger.LogDebug("Client disposed");
        }

        /// <inheritdoc/>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(BayeuxClient));
            
            if (_insideDispatch.Value)
                throw new InvalidOperationException(
                "ConnectAsync cannot be called from a message handler: the client's loop is waiting for the "
                + "handler to return, so it cannot be restarted from inside it. Call DisconnectAsync in the "
                + "handler, and reconnect from OnDisconnected, which runs after the loop has finished.");

            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);

            try
            {
                ChangeState(BayeuxClientState.Connecting);
                await SetUpConnectionAsync(call.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _bayeuxCts.Cancel();

                ChangeState(BayeuxClientState.Disconnected, ex);
                await SendDisconnectAsync().ConfigureAwait(false);
                throw;
            }

            _bayeuxTask = Task.Run(() => StartDialogueAsync());
        }

        /// <inheritdoc/>
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            if (_isDestroyed)
                throw new ObjectDisposedException(nameof(BayeuxClient));

            var currentTask = _bayeuxTask;

            if (currentTask != null)
            {
                _bayeuxCts.Cancel();

                if (_insideDispatch.Value)
                    return;

                if (cancellationToken.CanBeCanceled)
                {
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    await Task.WhenAny(currentTask, Task.Delay(Timeout.Infinite, call.Token)).ConfigureAwait(false);

                    call.Cancel();
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
                throw new ObjectDisposedException(nameof(BayeuxClient));

            if (string.IsNullOrEmpty(ClientId))
                throw new InvalidOperationException(
                    "No established session. Call ConnectAsync first, or wait: the client may be "
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
                    if (!_channels.TryAdd(channel.Key, channel.Value))
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
                                _channels.TryRemove(item, out _);
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
                            _channels.TryRemove(failedSubscribe.Key, out _);

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
                throw new ObjectDisposedException(nameof(BayeuxClient));

            if (string.IsNullOrEmpty(ClientId))
                throw new InvalidOperationException(
                    "No established session. Call ConnectAsync first, or wait: the client may be "
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
                    if (!_channels.TryRemove(channel, out var handler))
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
                                _channels.TryAdd(item.Key, item.Value);
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
                                _channels.TryAdd(failedUnsubscribe.Key, handler);
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

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Takes no <c>_channelLock</c>: the lock guards the subscription list, which a publish never
        /// touches. That is what makes it safe to await from a handler, while the loop waits on
        /// that handler.
        /// </para>
        /// <para>
        /// <c>ClientId</c> is read once: the loop may clear it between a check and a second read.
        /// The reply is matched by id, not channel, because the same response may carry this very
        /// message back to its publisher, on the same channel.
        /// </para>
        /// </remarks>
        public async Task PublishAsync(string channel, object? data, CancellationToken cancellationToken = default)
        {
            if (IsDestroyed)
                throw new ObjectDisposedException(nameof(BayeuxClient));

            ValidatePublishChannel(channel);

            var clientId = ClientId;

            if (string.IsNullOrEmpty(clientId))
                throw new InvalidOperationException(
                    "No established session. Call ConnectAsync first, or wait: the client may be "
                    + "re-establishing the session after the server invalidated it.");

            using var call = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            
            var jsonData = JsonSerializer.SerializeToElement(data, data?.GetType() ?? typeof(object), Options.JsonSerializerOptions);
            var request = new PublishRequestModel(clientId, channel, jsonData, GetNextId());
            var messages = await PostCometdAsync([request], call.Token).ConfigureAwait(false);

            var response = messages.FirstOrDefault(message => message.Id == request.RequestId && message.IsSuccessful is not null);

            if (response is null)
                throw new InvalidOperationException($"The server sent no reply to the publish on {channel}.");
            else if (response.IsSuccessful != true)
                throw new BayeuxPublishException(
                    channel, 
                    response.Error, 
                    response.Ext is null ? null : new ReadOnlyDictionary<string, JsonElement>(response.Ext));
        }

        /// <summary>Rejects a channel no message can be published to, before anything is sent.</summary>
        /// <param name="channel">The channel to check.</param>
        /// <exception cref="ArgumentException">
        /// The channel is missing or unrooted, a meta channel - reserved for the protocol - or a
        /// wildcard, which names a set of channels to subscribe to while a message goes to exactly
        /// one. A server would refuse all of these too; saying why here is kinder.
        /// </exception>
        private static void ValidatePublishChannel(string channel)
        {
            if (string.IsNullOrEmpty(channel) || channel[0] != '/')
                throw new ArgumentException("A channel starts with '/'.", nameof(channel));

            if (channel.StartsWith(MetaChannels.Meta, StringComparison.Ordinal))
                throw new ArgumentException("Meta channels cannot be published to.", nameof(channel));

            if (channel.EndsWith("/*", StringComparison.Ordinal) || channel.EndsWith("/**", StringComparison.Ordinal))
                throw new ArgumentException("Cannot publish to a wildcard channel.", nameof(channel));
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

                if (_channels.Count > 0)
                    await SubscribeChannelsAsync(clientId, _channels.Keys, FailureEndsConnection, cancellationToken).ConfigureAwait(false);

                ClientId = clientId;
            }
            finally
            {
                await ReleaseChannelLockAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The connect loop. Runs until the caller cancels, the server ends the session, or an
        /// error occurs that is not retried; then reports the outcome through the disconnected event.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A session is re-established in one place, at the top of an iteration, whatever lost it:
        /// the server invalidating it (402, <c>advice.reconnect: handshake</c>) or an error with a
        /// reconnect policy that allows another attempt. The error path waits first; the server's
        /// request is honoured after the usual <c>advice.interval</c>.
        /// </para>
        /// <para>
        /// Not every error loses the session. After a transport failure - no answer, a timeout, an
        /// HTTP error - the server still holds it until its <c>maxInterval</c>, with every event
        /// published meanwhile queued in it; a new handshake would throw that queue away. So the
        /// loop retries <c>/meta/connect</c> with the same id, and only a refusal - of the handshake
        /// or the connect - starts a new session. If the session did expire after all, the retried
        /// connect earns a 402, which leads to a new handshake the usual way. Keeping the session is
        /// also what lets <see cref="BayeuxAckExtension"/> have lost events sent again.
        /// </para>
        /// <para>
        /// The attempt count starts again only after a <c>/meta/connect</c> that lets polling
        /// continue. A 402 proves nothing about the new session, so it keeps the count: a server
        /// that accepts every handshake and refuses every connect still meets growing delays and
        /// <see cref="ReconnectOptions.MaxAttempts"/>.
        /// </para>
        /// <para>
        /// An error that is not retried leaves through the outer catch unchanged, so the event
        /// carries the real cause. A stop during a pending delay after a refusal finds
        /// <c>ClientId</c> already cleared, so nothing is sent for the lost session; after a
        /// transport failure the session may be alive, and is closed like any other.
        /// </para>
        /// <para>
        /// Clears <c>_bayeuxTask</c> before raising the event, so a consumer may call
        /// <see cref="ConnectAsync"/> from the handler without waiting on the task it is running in.
        /// </para>
        /// </remarks>
        private async Task StartDialogueAsync()
        {
            _isLoopRunning = true;
            var disconnectReason = DisconnectReason.TokenCancellation;
            Exception? failure = null;

            var attempts = 0;
            var needSetUp = false;
            var retrying = false;

            try
            {
                while (!_token.IsCancellationRequested)
                {
                    try
                    {
                        if (retrying || needSetUp)
                        {
                            if (Options.ReconnectOptions?.BeforeAttemptAsync is { } beforeAttemptAsync)
                                await beforeAttemptAsync(_token).ConfigureAwait(false);
                            
                            retrying = false;

                            if (needSetUp)
                            {
                                await SetUpConnectionAsync(_token).ConfigureAwait(false);
                                needSetUp = false;
                            }
                        }

                        var verdict = await ConnectOneAsync().ConfigureAwait(false);
                        
                        if (verdict == ConnectResult.Stop)
                        {
                            disconnectReason = DisconnectReason.ServerRequirement;

                            break;
                        }
                        else if (verdict == ConnectResult.Rehandshake)
                        {
                            ChangeState(BayeuxClientState.Reconnecting);
                            needSetUp = true;
                        }
                        else
                        {
                            // Connected only here, after a poll the server answered and let continue:
                            // a session set up by ConnectAsync is not yet proof that polling works.
                            ChangeState(BayeuxClientState.Connected);
                            attempts = 0;
                        }

                        await Task.Delay(ConnectInterval ?? 0, _token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        !_token.IsCancellationRequested && 
                        Options.ReconnectOptions is { } reconnectOptions && 
                        reconnectOptions.CanRetry(attempts) &&
                        ShouldRetrySafely(reconnectOptions, ex))
                    {
                        attempts++;
                        ChangeState(BayeuxClientState.Reconnecting, ex);

                        var delay = reconnectOptions.GetReconnectDelay(attempts, _random.NextDouble());

                        _logger.LogWarning(ex, 
                            "Connect for {ClientId} failed; attempt {Attempt} in {Delay}.",
                            ClientId, attempts, delay);

                        // A transport failure says nothing about the session: keep it, and retry the
                        // connect with the same id. Anything else - a refused handshake or connect -
                        // means it is gone.
                        if (ex is not HttpRequestException and not BayeuxHttpException and not OperationCanceledException)
                        {
                            // The session is lost. Clearing the id makes a subscribe meanwhile fail at
                            // once with "re-establishing", and tells the shutdown there is nothing to close.
                            ClientId = string.Empty;
                            needSetUp = true;
                        }

                        // BeforeAttemptAsync runs before the next attempt either way: credentials may
                        // have expired just as well while the same session is retried.
                        retrying = true;

                        await Task.Delay(delay, _token).ConfigureAwait(false);
                    }
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
                _isLoopRunning = false;
                _bayeuxTask = null;

                if (!_bayeuxCts.IsCancellationRequested)
                    _bayeuxCts.Cancel();

                ChangeState(BayeuxClientState.Disconnected, failure);
                await TryDisconnectAsync(disconnectReason, failure).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends one <c>/meta/connect</c> and turns the reply into a verdict for the loop.
        /// </summary>
        /// <returns>
        /// Whether to keep polling, re-establish the session, or stop. Transport failures are
        /// thrown, for the loop to retry or end on.
        /// </returns>
        /// <exception cref="BayeuxConnectException">
        /// The server refused the connect with advice <c>none</c>. A successful reply with that
        /// advice, or a <c>/meta/disconnect</c>, is a clean stop instead.
        /// </exception>
        /// <remarks>
        /// Also applies <c>advice.interval</c> and raises the one-off warning about several clients
        /// sharing a browser cookie.
        /// </remarks>
        private async Task<ConnectResult> ConnectOneAsync()
        {
            BayeuxResponseAdviceModel? advice = null;
            BayeuxResponseMessageModel? refusal = null;

            var verdict = ConnectResult.Continue;
            var messages = await PostCometdAsync([new ConnectRequestModel(ClientId, GetNextId(), _transport.ConnectionType)], _token).ConfigureAwait(false);

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

                    if (verdict == ConnectResult.Stop && message.IsSuccessful == false)
                        refusal = message;      
                }
                else if (message.Channel == MetaChannels.Disconnect)
                    verdict = ConnectResult.Stop;
            }

            if (verdict == ConnectResult.Rehandshake)
                _logger.LogInformation(
                    "Session {ClientId} invalidated by the server (advice: {Reconnect}); re-establishing",
                    ClientId, advice?.Reconnect ?? "none");

            // A refusal, not a goodbye: the server rejected this connect and advises that trying
            // the same again is pointless. Thrown rather than returned as Stop, so the cause
            // reaches the caller and a reconnect policy can decide - a Salesforce client, for one,
            // authenticates again and reconnects after 401::Authentication invalid.
            if (refusal is { } refused && verdict == ConnectResult.Stop)
            {
                // The server has already dropped the session: nothing to send a disconnect for.
                ClientId = string.Empty;

                throw RaiseOnError(
                ErrorSource.Connect,
                new BayeuxConnectException(
                    refused.Error, 
                    refused.Advice?.Reconnect,
                    refused.Ext is null ? null : new ReadOnlyDictionary<string, JsonElement>(refused.Ext)),
                FailureEndsConnection);
            }

            return verdict;
        }

        /// <summary>
        /// Checks that the transport's request timeout can outlast a held <c>/meta/connect</c>.
        /// </summary>
        /// <param name="serverTimeoutMs">The server's <c>advice.timeout</c>, or <c>null</c> if absent.</param>
        /// <exception cref="InvalidOperationException">The transport would time out before the server replies.</exception>
        /// <remarks>
        /// Without this the failure looks like a recurring network fault: every long poll aborts on
        /// a healthy connection once the HttpClient's timeout elapses.
        /// </remarks>
        private void ValidateClientTimeout(int? serverTimeoutMs)
        {
            if (_transport.RequestTimeout is not { } limit || serverTimeoutMs == null)
                return;

            var requiredSpan = TimeSpan.FromMilliseconds(serverTimeoutMs.Value) + TimeSpan.FromSeconds(5);

            if (limit < requiredSpan)
                throw RaiseOnError(
                    ErrorSource.Configuration,
                    $"Request timeout is {limit.TotalSeconds:0.#} seconds " +
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
        /// consumer's handler must not stop the client, and one failing handler must not deny the
        /// message to the others.
        /// </para>
        /// </remarks>
        private async Task HandleChannelMessageAsync(BayeuxResponseMessageModel message)
        {
            if (message.Channel is not { Length: > 0 } channel)
                return;

            var ext = message.Ext is null ? null : new ReadOnlyDictionary<string, JsonElement>(message.Ext);
            var @event = new BayeuxEvent(channel, message.Data, ext, Options.JsonSerializerOptions);
            var capturedToken = _token;

            // Never reset, and it does not need to be. A value set on an AsyncLocal inside an async
            // method flows into everything the method calls or awaits - the handlers - but not back
            // out to its caller: once this method returns, the caller sees what it saw before. The
            // flag is therefore true for the handlers and false again for the connect loop,
            // OnDisconnected included. It also flows into a Task.Run a handler starts, so a
            // fire-and-forget ConnectAsync from a handler is refused as well - rightly, since the
            // DisconnectAsync inside it would not wait for the old loop to finish.
            _insideDispatch.Value = true;

            foreach (var subscription in _channels)
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
                return channel.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal) && 
                    channel.LastIndexOf('/') == pattern.LastIndexOf('/');

            return false;
        }

        /// <summary>
        /// Ends the session and raises the disconnected event. Called once, as the loop exits.
        /// </summary>
        /// <param name="reason">Why the loop stopped.</param>
        /// <param name="failure">The error that stopped it, if any.</param>
        /// <remarks>
        /// <para>
        /// No disconnect request is sent for <see cref="DisconnectReason.ServerRequirement"/>: the
        /// server has already closed the session, so the request would only earn another error.
        /// Nor is one sent while a reconnect is pending: the loop cleared <c>ClientId</c> when the
        /// session was lost, and <see cref="SendDisconnectAsync"/> sends nothing without one.
        /// </para>
        /// <para>
        /// <c>ClientId</c> is cleared on every path, before the event, so a subscribe after the
        /// stop fails at once rather than reaching the server with the id of a finished session.
        /// </para>
        /// </remarks>
        private async Task TryDisconnectAsync(DisconnectReason reason, Exception? failure = null)
        {
            Exception? disconnectException = null;

            if (reason != DisconnectReason.ServerRequirement)
                disconnectException = await SendDisconnectAsync().ConfigureAwait(false);

            ClientId = string.Empty;

            RaiseOnDisconnected(BayeuxDisconnectedEventArgs.Create(reason, failure, disconnectException));
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
        /// Whether a rejection ends the client. True during session setup, where a channel the
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
            var messages = await PostCometdAsync([new HandshakeRequestModel(GetNextId(), _transport.ConnectionType)], cancellationToken, true).ConfigureAwait(false);

            var message = messages.FirstOrDefault(message => message.Channel == MetaChannels.Handshake);

            if (message == null)
                throw RaiseOnError(ErrorSource.Handshake, "Message in channel /meta/handshake was null.", FailureEndsConnection);

            if (message.IsSuccessful != true)
                throw RaiseOnError(ErrorSource.Handshake,
                    new BayeuxHandshakeException(
                        message.Error,
                        message.Advice?.Reconnect,
                        message.Ext is null ? null : new ReadOnlyDictionary<string, JsonElement>(message.Ext)),
                    FailureEndsConnection);

            if (message.SupportedConnectionTypes is { } supported && !supported.Contains(_transport.ConnectionType))
                throw RaiseOnError(ErrorSource.Configuration,
                    $"The server does not support the transport {_transport.ConnectionType}. Supported: {string.Join(", ", supported)}",
                    FailureEndsConnection);

            ValidateClientTimeout(message.Advice?.Timeout);

            if (!string.IsNullOrEmpty(message.ClientId))
            {
                _logger.LogInformation("Handshake succeeded with session {ClientId}", message.ClientId);

                return message.ClientId!;
            }

            throw RaiseOnError(ErrorSource.Handshake, "Failed to receive a valid clientId.", FailureEndsConnection);
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
            BayeuxRequestModel[] requestModel, 
            CancellationToken cancellationToken,
            bool deferred = false)
        {
            await ProcessOutgoingExtAsync(requestModel, cancellationToken).ConfigureAwait(false);

            try
            {
                var responseMessages = await _transport.SendAsync(requestModel, cancellationToken).ConfigureAwait(false);

                ProcessIncomingExt(responseMessages);

                if (deferred)
                    await ProcessMessagesAsync(responseMessages, deferred).ConfigureAwait(false);
                else
                    // No token: a token here only decides whether the delegate starts, and
                    // the reply has already arrived - the server will not send its events
                    // again. The caller's token would drop them when a publish is cancelled
                    // late; the session's is already cancelled when the disconnect reply
                    // comes in. Handlers still see cancellation through their own token.
                    await Task.Run(() => ProcessMessagesAsync(responseMessages, deferred)).ConfigureAwait(false);
                
                return responseMessages;
            }
            catch (Exception ex)
            {   
                if(!requestModel.Any(model => model is 
                    SubscribeRequestModel or 
                    DisconnectRequestModel or 
                    UnsubscribeRequestModel or 
                    PublishRequestModel))
                    RaiseOnError(
                        ErrorSource.Http,
                        ex,
                        FailureEndsConnection);
                throw;
            }
        }

        /// <summary>Delivers or queues every event in a response.</summary>
        /// <param name="messages">The messages the server sent back.</param>
        /// <param name="deferred">
        /// Queue the events for <see cref="ReleaseChannelLockAsync"/> instead of running handlers now.
        /// </param>
        /// <remarks>
        /// <para>
        /// Runs for every response, not only <c>/meta/connect</c>. CometD flushes the session queue
        /// onto whatever request arrives first, so a subscribe or unsubscribe reply can carry events
        /// ahead of the reply itself; filtering those out would lose them silently.
        /// </para>
        /// <para>
        /// An event is a non-meta message without <c>successful</c>. The reply to a publish is on
        /// the application channel too, but carries <c>successful</c> and no data; delivered as an
        /// event, it would reach a subscriber of that channel with empty data on every publish.
        /// </para>
        /// </remarks>
        private async Task ProcessMessagesAsync(BayeuxResponseMessageModel[] messages, bool deferred)
        {
            foreach (var message in messages)
            {   
                if (message.Channel is { } channel && 
                !channel.StartsWith(MetaChannels.Meta, StringComparison.Ordinal) && 
                message.IsSuccessful is null)
                {
                    if (deferred)
                        _messageQueue.Enqueue(message);
                    else
                        await HandleChannelMessageAsync(message).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Raises the disconnected event, swallowing anything the handler throws.</summary>
        /// <param name="args">The outcome to report.</param>
        /// <remarks>
        /// The handler runs while the loop is unwinding, so an exception escaping it would replace
        /// the real cause of the shutdown.
        /// </remarks>
        private void RaiseOnDisconnected(BayeuxDisconnectedEventArgs args)
        {
            try
            {
                OnDisconnected?.Invoke(this, args);
            }
            catch { }
        }

        /// <summary>Creates an exception, reports it through the error event, and returns it.</summary>
        /// <param name="source">Which operation failed.</param>
        /// <param name="message">The failure description.</param>
        /// <param name="isFatal">Whether the client stops because of it.</param>
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
                OnError?.Invoke(this, new BayeuxErrorEventArgs(exception, source, isFatal));
            }
            catch { }

            return exception;
        }

        /// <summary>Reports an existing exception through the error event and returns it.</summary>
        /// <param name="source">Which operation failed.</param>
        /// <param name="exception">The failure.</param>
        /// <param name="isFatal">Whether the client stops because of it.</param>
        /// <returns>The same exception, for the caller to throw or return.</returns>
        private Exception RaiseOnError(ErrorSource source, Exception exception, bool isFatal = false)
        {
            try
            {
                OnError?.Invoke(this, new BayeuxErrorEventArgs(exception, source, isFatal));
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
        private async Task ProcessOutgoingExtAsync(BayeuxRequestModel[] requestModel, CancellationToken cancellationToken)
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
                        throw RaiseOnError(
                            ErrorSource.Ext,
                            ex,
                            (request is ConnectRequestModel || request is HandshakeRequestModel) && FailureEndsConnection);
                    }
                }
            }
        }

        /// <summary>Lets every extension read every incoming message, before anything else does.</summary>
        /// <param name="messages">The messages the server sent back.</param>
        /// <remarks>
        /// A failing extension is reported and skipped, never rethrown: like a failing handler, one
        /// broken extension must not stop the client or deny the message to the rest.
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

        /// <summary>Asks the policy whether to retry, treating an exception from it as "no".</summary>
        /// <param name="reconnectOptions">The policy.</param>
        /// <param name="exception">The error that ended the attempt.</param>
        /// <returns>Whether to wait and try again.</returns>
        /// <remarks>
        /// Called from an exception filter, where anything the predicate threw would be swallowed
        /// without a trace. This way it is at least logged.
        /// </remarks>
        private bool ShouldRetrySafely(ReconnectOptions reconnectOptions, Exception exception)
        {
            try
            {
                return reconnectOptions.ShouldRetry(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception is thrown while ReconnectOptions.ShouldRetry has been processing.");
                return false;
            }
        }

        /// <summary>Moves to <paramref name="targetState"/> and reports it, if it is a change.</summary>
        /// <param name="targetState">The state to move to.</param>
        /// <param name="error">What caused the change, if an error did.</param>
        /// <remarks>
        /// <para>
        /// <c>Interlocked.Exchange</c> rather than a lock: it writes the new state and returns the
        /// old one in one step, so two threads changing state at once each see the true previous
        /// value, and only a real change is reported - the loop sets Connected after every
        /// successful poll. A lock would also be held while the event runs, and a subscriber
        /// calling back into the client from another thread could then deadlock on it.
        /// </para>
        /// <para>
        /// The event runs on the thread making the change - the loop's for most of them - under the
        /// same rules as a message handler: <c>_insideDispatch</c> is set for the call, so
        /// DisconnectAsync from a subscriber only signals instead of waiting for the loop that is
        /// waiting for it, and ConnectAsync throws. Restored afterwards, because this method is
        /// synchronous: a value set here stays in the context of the async method that called it -
        /// ConnectAsync, or the loop - for the rest of that method. From ConnectAsync it would flow
        /// through Task.Run into the loop, and from the loop into OnDisconnected, where
        /// reconnecting - the documented way - would then be refused.
        /// </para>
        /// </remarks>
        private void ChangeState(BayeuxClientState targetState, Exception? error = null)
        {
            var previousState = (BayeuxClientState)Interlocked.Exchange(ref _stateCode, (int)targetState);

            if (previousState == targetState)
                return;

            var capturedInsideDispatch = _insideDispatch.Value;
            _insideDispatch.Value = true;

            try
            {
                OnStateChanged?.Invoke(this, new BayeuxStateChangedEventArgs(previousState, targetState, error));
            }
            catch { }   // a subscriber's bug must not break the loop, as with the other events
            finally
            {
                _insideDispatch.Value = capturedInsideDispatch;
            }
        }
    }
}