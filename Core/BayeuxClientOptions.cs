// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.ObjectModel;
using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>Configuration for a <see cref="BayeuxClient"/>.</summary>
    public sealed class BayeuxClientOptions
    {
        /// <summary>
        /// The channels a client starts with, each with the handler invoked for its messages.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the starting set. Each client copies it when it is created and keeps its own list
        /// from then on - <see cref="IBayeuxClient.Channels"/> - so subscribing or unsubscribing
        /// on one client never shows here or in another client built from these options.
        /// </para>
        /// <para>
        /// Keys may be exact channel names or Bayeux patterns: <c>/a/*</c> matches one further
        /// segment, <c>/a/**</c> any depth below.
        /// </para>
        /// </remarks>
        public IReadOnlyDictionary<string, BayeuxEventHandler> Channels { get; }

        /// <summary>
        /// How long to wait for the <c>/meta/disconnect</c> request during shutdown. Defaults to
        /// seven seconds.
        /// </summary>
        public TimeSpan DisconnectTimeout { get; }

        /// <summary>
        /// How long a reply may take beyond what the server itself spends on it, before a WebSocket
        /// is given up as dead. Ten seconds by default.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A dead connection - a dropped network, a NAT that forgot it - often says nothing: no error,
        /// no close, sends still succeed. Only the missing reply tells. A <c>/meta/connect</c> may take
        /// the server's <c>advice.timeout</c> plus this; any other request, this alone. Past it, the
        /// socket is aborted, every request on it fails with <c>WebSocketException</c>, and the next
        /// one opens a new socket - in the same session, with <see cref="ReconnectOptions"/>.
        /// </para>
        /// <para>
        /// Too short, and a slow but healthy network loses its socket over and over; too long, and a
        /// dead one goes unnoticed for as long. Long-polling does not use it: there,
        /// <see cref="HttpClient.Timeout"/> plays this part.
        /// </para>
        /// </remarks>
        public TimeSpan MaxNetworkDelay { get; }

        /// <summary>
        /// Path of the Bayeux endpoint, relative to the HTTP client's base address. Message names
        /// are appended to it, producing URIs such as <c>{Path}/connect</c>.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Extensions that read and write the <c>ext</c> field of every message, in the order given.
        /// Empty when none were supplied.
        /// </summary>
        public IReadOnlyList<IBayeuxExtension> Extensions { get; }

        /// <summary>
        /// How to re-establish a session that an error ended, or <c>null</c> - the default - for no
        /// reconnection: the first error stops the client and is reported by the disconnect event.
        /// </summary>
        /// <remarks>
        /// Shared, not copied: it is immutable, so one instance can serve any number of clients.
        /// </remarks>
        public ReconnectOptions? ReconnectOptions { get; }

        /// <summary>
        /// How application data is written and read: what <c>PublishAsync</c> sends, and what
        /// <see cref="BayeuxEvent.GetData{T}"/> and <see cref="BayeuxHandler.Of{T}"/> read.
        /// <see cref="JsonSerializerDefaults.Web"/> by default: camelCase names, read without regard
        /// to case - what servers written in JavaScript send and expect.
        /// </summary>
        /// <remarks>
        /// The protocol's own fields are unaffected; they keep the names the protocol defines.
        /// Shared, not copied: System.Text.Json freezes an options object on first use and caches
        /// type metadata in it, so one instance serving every client is both safe and cheaper.
        /// </remarks>
        public JsonSerializerOptions JsonSerializerOptions { get; }

        /// <summary>
        /// How messages travel: <see cref="BayeuxTransportType.LongPolling"/> by default. The server
        /// must offer the same transport; the handshake fails with a configuration error otherwise.
        /// </summary>
        public BayeuxTransportType TransportType { get; }

        /// <summary>Creates options for a client.</summary>
        /// <param name="channels">
        /// Channels to subscribe to on connect, with their handlers. Copied, so later changes to
        /// the collection you pass have no effect.
        /// </param>
        /// <param name="path">Path of the Bayeux endpoint.</param>
        /// <param name="disconnectTimeout">
        /// How long to wait for the disconnect request, or <c>null</c> for seven seconds.
        /// </param>
        /// <param name="extensions">
        /// Extensions for the <c>ext</c> field, or <c>null</c> for none. Copied, like the channels.
        /// </param>
        /// <param name="reconnectOptions">
        /// How to reconnect after an error, or <c>null</c> for no reconnection.
        /// </param>
        /// <param name="jsonSerializerOptions">
        /// How application data is serialized, or <c>null</c> for <see cref="JsonSerializerDefaults.Web"/>.
        /// </param>
        /// <param name="transportType">
        /// The transport, or <c>null</c> for <see cref="BayeuxTransportType.LongPolling"/>.
        /// </param>
        /// <param name="maxNetworkDelay">
        /// How long a reply may take beyond the server's own hold before a WebSocket is presumed
        /// dead; <c>null</c> for ten seconds. See <see cref="MaxNetworkDelay"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="extensions"/> contains <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxNetworkDelay"/> is zero or negative.</exception>
        /// <remarks>
        /// One constructor with optional parameters rather than an overload per combination: the
        /// public API analyzer rejects more than one overload with optional parameters (RS0026),
        /// because each further parameter would risk making existing calls ambiguous.
        /// </remarks>
        public BayeuxClientOptions(
            IEnumerable<KeyValuePair<string, BayeuxEventHandler>> channels,
            string path,
            TimeSpan? disconnectTimeout = null,
            IEnumerable<IBayeuxExtension>? extensions = null,
            ReconnectOptions? reconnectOptions = null,
            JsonSerializerOptions? jsonSerializerOptions = null,
            BayeuxTransportType? transportType = null,
            TimeSpan? maxNetworkDelay = null)
        {
            if (channels is null)
                throw new ArgumentNullException(nameof(channels));

            Channels = new ReadOnlyDictionary<string, BayeuxEventHandler>(channels.ToDictionary(pair => pair.Key, pair => pair.Value));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            DisconnectTimeout = disconnectTimeout ?? TimeSpan.FromMilliseconds(7000);
            ReconnectOptions = reconnectOptions;
            JsonSerializerOptions = jsonSerializerOptions ?? new(JsonSerializerDefaults.Web);
            TransportType = transportType ?? BayeuxTransportType.LongPolling;
            MaxNetworkDelay = maxNetworkDelay ?? TimeSpan.FromSeconds(10);

            if (MaxNetworkDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxNetworkDelay), "Value must be greater than 0");

            var copy = extensions?.ToArray() ?? Array.Empty<IBayeuxExtension>();

            if (copy.Contains(null))
                throw new ArgumentException("Extensions must not contain null.", nameof(extensions));

            Extensions = copy;
        }
    }
}
