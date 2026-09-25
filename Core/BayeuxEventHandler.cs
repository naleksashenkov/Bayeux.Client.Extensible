// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Handles one event delivered on a subscribed channel.
    /// </summary>
    /// <param name="message">The event: the channel it arrived on, its data and any <c>ext</c>.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the poller stops. Pass it to whatever the handler awaits.
    /// </param>
    /// <returns>A task that completes when the event has been handled.</returns>
    /// <remarks>
    /// <para>
    /// <b>Handlers are awaited.</b> The next long poll is not sent until every handler for the
    /// current events has finished, so a slow handler delays every event behind it - the server
    /// holds them meanwhile. A handler that runs longer than the server's session timeout (CometD's
    /// <c>maxInterval</c>, 10 seconds by default) lets the session expire, and whatever the server
    /// was holding for it is lost. Keep handlers short; hand long work to a queue of your own.
    /// </para>
    /// <para>
    /// <b>Honour the token.</b> <c>DisconnectAsync</c> and <c>DisposeAsync</c> wait for a running
    /// handler, so one that ignores the token holds up shutdown for as long as it runs. An
    /// <see cref="OperationCanceledException"/> caused by the token is expected, not reported.
    /// </para>
    /// <para>
    /// <b>Exceptions</b> are reported through <c>OnError</c> as <c>ErrorSource.Handler</c> and do not
    /// stop the poller. Every other handler for the same event still receives it.
    /// </para>
    /// <para>
    /// <b>Calling the poller from a handler.</b> <c>SubscribeNewChannelsAsync</c> and
    /// <c>UnsubscribeChannelsAsync</c> may be awaited. <c>DisconnectAsync</c> may be awaited too,
    /// but from here it only signals: the loop is waiting for this handler, so it stops once the
    /// handler returns. <c>ConnectAsync</c> throws <see cref="InvalidOperationException"/> - reconnect
    /// from <c>OnPollerDisconnected</c>, which runs after the loop has finished.
    /// </para>
    /// <para>
    /// A handler may run on any thread; do not rely on a particular thread or synchronization
    /// context. A handler with nothing to await returns <see cref="Task.CompletedTask"/>.
    /// </para>
    /// </remarks>
    public delegate Task BayeuxEventHandler(BayeuxEvent message, CancellationToken cancellationToken);

    /// <summary>
    /// One event as a handler sees it: the channel it arrived on, its data and any extension data.
    /// </summary>
    /// <remarks>
    /// Deliberately smaller than the protocol message: a handler needs the event, not the
    /// transport's bookkeeping. One instance is shared by every handler the event is delivered to.
    /// </remarks>
    public sealed class BayeuxEvent
    {
        /// <summary>
        /// The concrete channel the event arrived on. For a pattern subscription such as
        /// <c>/topic/**</c>, this says which of the matching channels it was.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// The event's payload - the message's <c>data</c> field - as raw JSON. It may be kept after
        /// the handler returns.
        /// </summary>
        public JsonElement Data { get; }

        /// <summary>
        /// Extension data the server attached to this event, keyed by extension name, or
        /// <c>null</c> when it sent none. Read-only: every handler for the event sees the same data.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement>? Ext { get; }

        /// <summary>Creates an event.</summary>
        /// <param name="channel">The channel the event arrived on.</param>
        /// <param name="data">The event's payload.</param>
        /// <param name="ext">Extension data, or <c>null</c>.</param>
        /// <remarks>
        /// The poller creates these for real deliveries. The constructor is public so that a
        /// handler can be unit-tested by calling it with an event built by the test.
        /// </remarks>
        public BayeuxEvent(string channel, JsonElement data, IReadOnlyDictionary<string, JsonElement>? ext)
        {
            Channel = channel;
            Data = data;
            Ext = ext;
        }
    }
}
