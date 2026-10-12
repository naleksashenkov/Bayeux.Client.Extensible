// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Authentication;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// A Bayeux client. One instance owns exactly one Bayeux session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client runs in the background: <see cref="ConnectAsync"/> returns once the session is
    /// established, messages are delivered to the handlers registered in
    /// <see cref="Channels"/>, and <see cref="OnDisconnected"/> reports that
    /// the loop has ended. Nothing needs to be awaited in between.
    /// </para>
    /// <para>
    /// Errors are not retried. Any failure ends the session and raises
    /// <see cref="OnDisconnected"/> with <see cref="DisconnectReason.Failed"/>; reconnecting is
    /// the caller's decision.
    /// </para>
    /// <para>
    /// Instance members are not thread-safe. Drive the lifecycle from a single thread.
    /// </para>
    /// </remarks>
    public interface IBayeuxClient : IDisposable, IAsyncDisposable
    {
        /// <summary>The provider that authenticates outgoing requests.</summary>
        IAuthProvider AuthProvider { get; }

        /// <summary>The configuration this client was created with.</summary>
        BayeuxClientOptions Options { get; }

        /// <summary>Whether the client has been disposed and can no longer be used.</summary>
        bool IsDestroyed { get; }

        /// <summary>
        /// The Bayeux session id of the established session, or an empty string when there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Non-empty means the whole session is ready: the handshake succeeded <em>and</em> every
        /// configured channel was subscribed. It is not set as soon as the server issues the id
        /// &#8212; during setup the handshake has already returned one while this is still empty,
        /// because a session whose subscriptions failed is not a session the caller asked for.
        /// </para>
        /// <para>
        /// It empties and fills again on every re-handshake inside the connect loop, so an empty
        /// value on a running client is normal recovery, not a failure. Treat it as the session id
        /// to correlate with server logs, not as a connection flag: it is written by the client's
        /// own task and read by yours, with no synchronisation between them. Whether the client is
        /// connected is what <see cref="State"/> is for.
        /// </para>
        /// </remarks>
        string ClientId { get; }

        /// <summary>
        /// Raised once when the connect loop stops, with the reason and any error involved.
        /// </summary>
        /// <remarks>
        /// Raised only after a session was established; a failed <see cref="ConnectAsync"/> throws
        /// instead. The handler runs on the client's own task, after the loop has finished, so it may
        /// call <see cref="ConnectAsync"/> directly to reconnect. Exceptions thrown by the handler are
        /// swallowed. Note that reconnecting here with no delay retries as fast as the server can
        /// refuse &#8212; pace it yourself.
        /// </remarks>
        event EventHandler<BayeuxDisconnectedEventArgs> OnDisconnected;

        /// <summary>
        /// Raised for each error the client observes. May fire any number of times;
        /// <see cref="BayeuxErrorEventArgs.IsFatal"/> says whether the client is about to stop.
        /// </summary>
        /// <remarks>Exceptions thrown by subscribers are swallowed.</remarks>
        event EventHandler<BayeuxErrorEventArgs>? OnError;

        /// <summary>Where the client is in its life: see <see cref="BayeuxClientState"/>.</summary>
        /// <remarks>
        /// <para>
        /// <see cref="BayeuxClientState.Connected"/> means a <c>/meta/connect</c> has succeeded, not merely
        /// that <see cref="ConnectAsync"/> returned: between the two the state is still
        /// <see cref="BayeuxClientState.Connecting"/>. Servers answer the first connect of a session at
        /// once, so the gap is short.
        /// </para>
        /// <para>
        /// A snapshot: by the time a caller acts on it the client may have moved on. To follow the
        /// changes, use <see cref="OnStateChanged"/>.
        /// </para>
        /// </remarks>
        BayeuxClientState State { get; }

        /// <summary>Raised on every change of <see cref="State"/>, and only on a change.</summary>
        /// <remarks>
        /// <para>
        /// Runs on the thread that made the change - for most changes the connect loop - before the
        /// client goes on, so a subscriber should be quick. The same rules as a message handler
        /// apply: <see cref="DisconnectAsync"/> from a subscriber only signals the stop, and
        /// <see cref="ConnectAsync"/> throws. Reconnect from <see cref="OnDisconnected"/>,
        /// which is raised after the change to <see cref="BayeuxClientState.Disconnected"/>.
        /// </para>
        /// <para>Exceptions thrown by subscribers are swallowed.</para>
        /// </remarks>
        event EventHandler<BayeuxStateChangedEventArgs>? OnStateChanged;

        /// <summary>
        /// The channels this client is subscribed to now, each with the handler invoked for its
        /// messages. Resubscribed automatically after every handshake, so the set survives a
        /// reconnect.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Starts as a copy of <see cref="BayeuxClientOptions.Channels"/> and belongs to this client alone:
        /// two clients built from one options object never see each other's subscriptions.
        /// </para>
        /// <para>
        /// Keys may be exact channel names or Bayeux patterns: <c>/a/*</c> matches one further
        /// segment, <c>/a/**</c> any depth below. Overlapping patterns each receive the message, so
        /// registering both <c>/a/**</c> and <c>/a/b</c> means two handler calls for one message,
        /// in no defined order.
        /// </para>
        /// <para>
        /// Read-only by design. Channels are added and removed through
        /// <see cref="SubscribeNewChannelsAsync"/> and <see cref="UnsubscribeChannelsAsync"/>, which
        /// also tell the server; a bare dictionary entry would never be subscribed. A live view,
        /// not a snapshot: it changes as they succeed.
        /// </para>
        /// </remarks>
        IReadOnlyDictionary<string, BayeuxEventHandler> Channels { get; }

        /// <summary>
        /// Stops any current session, performs the handshake and initial subscriptions, then starts
        /// polling in the background.
        /// </summary>
        /// <param name="cancellationToken">
        /// Stops waiting for the session to be established. Cancelled during setup, the handshake and
        /// subscriptions are abandoned, any half-created session is released, and
        /// <see cref="OperationCanceledException"/> is thrown. It has no say over the session once it is
        /// running: that lasts until <see cref="DisconnectAsync"/>, disposal, or the server ends it.
        /// </param>
        /// <returns>A task that completes once the session is established and polling has started.</returns>
        /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before setup completed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The handshake was rejected, returned no client id, or the server's hold time exceeds the
        /// HTTP client's timeout. Also thrown, before anything is sent, when called from inside a
        /// message handler: the loop is waiting for that handler, so it cannot be restarted from there.
        /// Reconnect from <see cref="OnDisconnected"/> instead.
        /// </exception>
        /// <exception cref="BayeuxSubscriptionException">
        /// The server rejected one or more of the configured channels. Setup does not continue with a
        /// partial subscription set: a session missing a channel the caller asked for is not the
        /// session they requested.
        /// </exception>
        /// <remarks>
        /// Setup failures are reported by throwing, not through <see cref="OnDisconnected"/>,
        /// because no session was established. Any half-created session is released before the
        /// exception propagates.
        /// </remarks>
        Task ConnectAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribes the running session to further channels and registers their handlers. They are
        /// re-subscribed automatically after later handshakes.
        /// </summary>
        /// <param name="channels">
        /// The channel names, each with the handler for its events. See <see cref="BayeuxEventHandler"/>
        /// for what a handler may rely on - above all, that it is awaited.
        /// </param>
        /// <param name="cancellationToken">
        /// Stops waiting for this call: for the channel lock, and for the server's answer. Cancelled
        /// while the request is in flight, nothing is known about what the server did, so the whole
        /// batch is rolled back as for any other failed request. Stopping the session cancels the call
        /// as well.
        /// </param>
        /// <returns>A task that completes once the server has accepted the subscriptions.</returns>
        /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled, or the session stopped, before the server answered.
        /// Nothing from this batch is subscribed.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="channels"/> is <c>null</c> or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// There is no established session &#8212; either the client was never connected, or it is
        /// re-establishing the session after the server invalidated it &#8212; or every requested
        /// channel was already registered.
        /// </exception>
        /// <exception cref="BayeuxSubscriptionException">
        /// The server answered and rejected some of the channels. The rest are subscribed.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The whole set is sent as one Bayeux message, so it costs one round trip however many
        /// channels it holds. Channels already registered are skipped rather than re-sent.
        /// </para>
        /// <para>
        /// Two failures are distinguished. If the server answers and rejects part of the batch, the
        /// rejected channels are rolled back and <see cref="BayeuxSubscriptionException"/> names both
        /// sides &#8212; the accepted channels stay subscribed. If the request itself fails, nothing is
        /// known about what the server did, so the entire batch is rolled back and the transport
        /// exception propagates unchanged.
        /// </para>
        /// <para>
        /// Wildcard patterns are supported: <c>/a/*</c> matches one further segment, <c>/a/**</c> any
        /// depth below. Patterns that overlap each deliver the message, so a message on <c>/a/b</c>
        /// reaches handlers registered for both <c>/a/**</c> and <c>/a/b</c>.
        /// </para>
        /// </remarks>
        Task SubscribeNewChannelsAsync(IDictionary<string, BayeuxEventHandler> channels, CancellationToken cancellationToken = default);

        /// <summary>
        /// Unsubscribes from channels and removes their handlers. They are no longer re-subscribed
        /// after later handshakes.
        /// </summary>
        /// <param name="channels">The channel names or patterns exactly as they were subscribed.</param>
        /// <param name="cancellationToken">
        /// Stops waiting for this call: for the channel lock, and for the server's answer. Cancelled
        /// while the request is in flight, every handler in the batch is put back, since the server may
        /// still consider those subscriptions live. Stopping the session cancels the call as well.
        /// </param>
        /// <returns>A task that completes once the server has accepted the unsubscribes.</returns>
        /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled, or the session stopped, before the server answered.
        /// Every channel in the batch is still subscribed.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="channels"/> is <c>null</c> or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// There is no established session &#8212; either the client was never connected, or it is
        /// re-establishing the session after the server invalidated it &#8212; or none of the requested
        /// channels was registered.
        /// </exception>
        /// <exception cref="BayeuxSubscriptionException">
        /// The server answered and refused to drop some of the channels. The rest are unsubscribed.
        /// </exception>
        /// <remarks>
        /// <para>
        /// The whole set is sent as one Bayeux message. Patterns are matched by exact text, so
        /// unsubscribing <c>/topic/**</c> leaves a separate <c>/topic/orders</c> in place.
        /// </para>
        /// <para>
        /// Rollback works the same way as for subscribing, and matters more here: a handler whose
        /// unsubscribe was refused is put back, because the server still considers the subscription
        /// live and will keep sending its messages.
        /// </para>
        /// </remarks>
        Task UnsubscribeChannelsAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops the connect loop, waits for it to finish, and releases the session on the server.
        /// </summary>
        /// <param name="cancellationToken">
        /// Bounds only the wait. The stop itself cannot be cancelled: it is signalled before the wait
        /// begins, and the loop goes on stopping in the background if the caller stops waiting.
        /// </param>
        /// <returns>A task that completes once the loop has stopped.</returns>
        /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled before the loop finished. The loop is still stopping.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Safe to call when nothing is running. <see cref="OnDisconnected"/> is raised by the
        /// loop as it ends, with <see cref="DisconnectReason.TokenCancellation"/>. A running handler is
        /// waited for; its cancellation token is cancelled first.
        /// </para>
        /// <para>
        /// Called from inside a message handler, it signals and returns without waiting: the loop is
        /// waiting for that handler, and stops once it returns.
        /// </para>
        /// </remarks>
        Task DisconnectAsync(CancellationToken cancellationToken = default);

        /// <summary>Publishes one message to an application channel.</summary>
        /// <param name="channel">
        /// The channel, such as <c>/chat/room1</c>, or a <c>/service/</c> channel to address the
        /// server itself. Not a meta channel, and not a wildcard.
        /// </param>
        /// <param name="data">
        /// The payload, serialized with <see cref="BayeuxClientOptions.JsonSerializerOptions"/> -
        /// camelCase names by default, so a property <c>OrderId</c> goes out as <c>orderId</c>. A
        /// <see cref="System.Text.Json.JsonElement"/> is sent as it is.
        /// </param>
        /// <param name="cancellationToken">
        /// Stops waiting for the reply. It cannot take back a message already sent: the server may
        /// have delivered it anyway.
        /// </param>
        /// <returns>A task that completes when the server has accepted the message.</returns>
        /// <exception cref="ArgumentException">The channel is missing, unrooted, a meta channel or a wildcard.</exception>
        /// <exception cref="InvalidOperationException">
        /// There is no session - not connected yet, or being re-established - or the server sent
        /// no reply, which the protocol requires.
        /// </exception>
        /// <exception cref="BayeuxPublishException">The server refused the message.</exception>
        /// <exception cref="BayeuxHttpException">The server answered with an HTTP error.</exception>
        /// <exception cref="ObjectDisposedException">The client has been disposed.</exception>
        /// <exception cref="OperationCanceledException">The token was cancelled, or the client stopped.</exception>
        /// <remarks>
        /// <para>
        /// <b>Never retried,</b> not even with <see cref="BayeuxClientOptions.ReconnectOptions"/>. A publish
        /// is not idempotent: repeating one after a timeout may deliver it twice. Whether to try
        /// again is the caller's decision. Failures are thrown, not reported through
        /// <see cref="OnError"/>, and do not affect the session.
        /// </para>
        /// <para>
        /// <b>A subscriber gets its own messages.</b> CometD delivers to every subscriber of the
        /// channel, the publisher included - sometimes inside the reply to the publish itself, in
        /// which case the handler has run before this returns.
        /// </para>
        /// <para>
        /// Safe to await from a handler: publishing takes no lock the loop could be holding.
        /// </para>
        /// <para>
        /// Not every server accepts publishes from clients. The Salesforce Streaming API does not:
        /// events are published there through its REST API.
        /// </para>
        /// </remarks>
        Task PublishAsync(string channel, object? data, CancellationToken cancellationToken = default);
    }   
}