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
    /// Cancelled when the client stops. Pass it to whatever the handler awaits.
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
    /// stop the client. Every other handler for the same event still receives it.
    /// </para>
    /// <para>
    /// <b>Calling the client from a handler.</b> <c>SubscribeNewChannelsAsync</c> and
    /// <c>UnsubscribeChannelsAsync</c> may be awaited. <c>DisconnectAsync</c> may be awaited too,
    /// but from here it only signals: the loop is waiting for this handler, so it stops once the
    /// handler returns. <c>ConnectAsync</c> throws <see cref="InvalidOperationException"/> - reconnect
    /// from <c>OnDisconnected</c>, which runs after the loop has finished.
    /// </para>
    /// <para>
    /// A handler may run on any thread; do not rely on a particular thread or synchronization
    /// context. A handler with nothing to await returns <see cref="Task.CompletedTask"/>.
    /// </para>
    /// </remarks>
    public delegate Task BayeuxEventHandler(BayeuxEvent message, CancellationToken cancellationToken);

    /// <summary>Builds handlers that receive the event's data as a typed object.</summary>
    public static class BayeuxHandler
    {
        /// <summary>
        /// Wraps a handler that wants <typeparamref name="T"/> into a <see cref="BayeuxEventHandler"/>
        /// the client can call.
        /// </summary>
        /// <typeparam name="T">The type the event's data is read as.</typeparam>
        /// <param name="handler">
        /// Receives the data, the whole event - for its channel or <c>ext</c> - and the token.
        /// </param>
        /// <returns>A handler for <see cref="BayeuxClientOptions"/> or <c>SubscribeNewChannelsAsync</c>.</returns>
        /// <remarks>
        /// <para>
        /// The data is read for every event, when it is delivered, with the client's
        /// <see cref="BayeuxClientOptions.JsonSerializerOptions"/>. Nothing in the client changes: to it
        /// this is an ordinary handler.
        /// </para>
        /// <para>
        /// Data that does not fit <typeparamref name="T"/> throws a <see cref="JsonException"/>,
        /// which is reported like any handler exception - through <c>OnError</c> as
        /// <see cref="ErrorSource.Handler"/> - and the client carries on. A <c>null</c> payload
        /// reaches the handler as <c>null</c>.
        /// </para>
        /// <code>
        /// channels["/orders/new"] = BayeuxHandler.Of&lt;Order&gt;(async (order, e, ct) =&gt; await SaveAsync(order, ct));
        /// </code>
        /// </remarks>
        public static BayeuxEventHandler Of<T>(Func<T, BayeuxEvent, CancellationToken, Task> handler) =>
        (element, ct) => handler(element.GetData<T>()!, element, ct);
    }

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

        // The client's, so typed reads follow the same rules as everything it sends.
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        private static readonly JsonSerializerOptions _defaultJsonSerializerOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Reads <see cref="Data"/> as <typeparamref name="T"/>.</summary>
        /// <typeparam name="T">The type to read the data as.</typeparam>
        /// <returns>The data, or <c>null</c> when the payload is JSON <c>null</c>.</returns>
        /// <exception cref="JsonException">The data does not fit <typeparamref name="T"/>.</exception>
        /// <remarks>
        /// Uses the client's <see cref="BayeuxClientOptions.JsonSerializerOptions"/> - camelCase names by
        /// default. Reads afresh on every call; keep the result rather than calling it repeatedly.
        /// </remarks>
        public T? GetData<T>() => Data.Deserialize<T>(_jsonSerializerOptions);

        /// <summary>Creates an event.</summary>
        /// <param name="channel">The channel the event arrived on.</param>
        /// <param name="data">The event's payload.</param>
        /// <param name="ext">Extension data, or <c>null</c>.</param>
        /// <param name="jsonSerializerOptions">
        /// How <see cref="GetData{T}"/> reads the data, or <c>null</c> for the client's default:
        /// <see cref="JsonSerializerDefaults.Web"/>.
        /// </param>
        /// <remarks>
        /// The client creates these for real deliveries. The constructor is public so that a
        /// handler can be unit-tested by calling it with an event built by the test; the options
        /// come last and are optional so that such tests need not mention them.
        /// </remarks>
        public BayeuxEvent(
            string channel, 
            JsonElement data,  
            IReadOnlyDictionary<string, JsonElement>? ext,
            JsonSerializerOptions? jsonSerializerOptions = null)
        {
            Channel = channel;
            Data = data;
            Ext = ext;
            _jsonSerializerOptions = jsonSerializerOptions ?? _defaultJsonSerializerOptions;
        }
    }
}
