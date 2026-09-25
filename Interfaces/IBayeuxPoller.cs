// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Interfaces;

/// <summary>
/// A Bayeux long-polling client. One instance owns exactly one CometD session.
/// </summary>
/// <remarks>
/// <para>
/// The poller runs in the background: <see cref="ConnectAsync"/> returns once the session is
/// established, messages are delivered to the handlers registered in
/// <see cref="CometdPollerOptions.Channels"/>, and <see cref="OnPollerDisconnected"/> reports that
/// the loop has ended. Nothing needs to be awaited in between.
/// </para>
/// <para>
/// Errors are not retried. Any failure ends the session and raises
/// <see cref="OnPollerDisconnected"/> with <see cref="DisconnectReason.Failed"/>; reconnecting is
/// the caller's decision.
/// </para>
/// <para>
/// Instance members are not thread-safe. Drive the lifecycle from a single thread.
/// </para>
/// </remarks>
public interface IBayeuxPoller : IDisposable, IAsyncDisposable
{
    /// <summary>The provider that authenticates outgoing requests.</summary>
    IAuthProvider AuthProvider { get; }

    /// <summary>The configuration this poller was created with.</summary>
    CometdPollerOptions Options { get; }

    /// <summary>Whether the poller has been disposed and can no longer be used.</summary>
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
    /// It empties and fills again on every re-handshake inside the polling loop, so an empty
    /// value on a running poller is normal recovery, not a failure. Treat it as the session id
    /// to correlate with server logs, not as a connection flag to poll: it is written by the
    /// poller's own task and read by yours, with no synchronisation between them.
    /// </para>
    /// </remarks>
    string ClientId { get; }

    /// <summary>
    /// Raised once when the polling loop stops, with the reason and any error involved.
    /// </summary>
    /// <remarks>
    /// Raised only after a session was established; a failed <see cref="ConnectAsync"/> throws
    /// instead. The handler runs on the poller's own task, after the loop has finished, so it may
    /// call <see cref="ConnectAsync"/> directly to reconnect. Exceptions thrown by the handler are
    /// swallowed. Note that reconnecting here with no delay retries as fast as the server can
    /// refuse &#8212; pace it yourself.
    /// </remarks>
    event EventHandler<OnPollerDisconnectedEventArgs> OnPollerDisconnected;

    /// <summary>
    /// Raised for each error the poller observes. May fire any number of times;
    /// <see cref="OnPollerErrorEventArgs.IsFatal"/> says whether the poller is about to stop.
    /// </summary>
    /// <remarks>Exceptions thrown by subscribers are swallowed.</remarks>
    event EventHandler<OnPollerErrorEventArgs>? OnError;

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
    /// <exception cref="ObjectDisposedException">The poller has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before setup completed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The handshake was rejected, returned no client id, or the server's hold time exceeds the
    /// HTTP client's timeout. Also thrown, before anything is sent, when called from inside a
    /// message handler: the loop is waiting for that handler, so it cannot be restarted from there.
    /// Reconnect from <see cref="OnPollerDisconnected"/> instead.
    /// </exception>
    /// <exception cref="BayeuxSubscriptionException">
    /// The server rejected one or more of the configured channels. Setup does not continue with a
    /// partial subscription set: a session missing a channel the caller asked for is not the
    /// session they requested.
    /// </exception>
    /// <remarks>
    /// Setup failures are reported by throwing, not through <see cref="OnPollerDisconnected"/>,
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
    /// <exception cref="ObjectDisposedException">The poller has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, or the session stopped, before the server answered.
    /// Nothing from this batch is subscribed.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="channels"/> is <c>null</c> or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no established session &#8212; either the poller was never connected, or it is
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
    /// <exception cref="ObjectDisposedException">The poller has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, or the session stopped, before the server answered.
    /// Every channel in the batch is still subscribed.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="channels"/> is <c>null</c> or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// There is no established session &#8212; either the poller was never connected, or it is
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
    /// Stops the polling loop, waits for it to finish, and releases the session on the server.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds only the wait. The stop itself cannot be cancelled: it is signalled before the wait
    /// begins, and the loop goes on stopping in the background if the caller stops waiting.
    /// </param>
    /// <returns>A task that completes once the loop has stopped.</returns>
    /// <exception cref="ObjectDisposedException">The poller has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the loop finished. The loop is still stopping.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Safe to call when nothing is running. <see cref="OnPollerDisconnected"/> is raised by the
    /// loop as it ends, with <see cref="DisconnectReason.TokenCancellation"/>. A running handler is
    /// waited for; its cancellation token is cancelled first.
    /// </para>
    /// <para>
    /// Called from inside a message handler, it signals and returns without waiting: the loop is
    /// waiting for that handler, and stops once it returns.
    /// </para>
    /// </remarks>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
