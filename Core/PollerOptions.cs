// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.ObjectModel;
using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>Configuration for a <see cref="CometDPoller"/>.</summary>
    public sealed class PollerOptions
    {
        /// <summary>
        /// The channels a poller starts with, each with the handler invoked for its messages.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the starting set. Each poller copies it when it is created and keeps its own list
        /// from then on - <see cref="IBayeuxPoller.Channels"/> - so subscribing or unsubscribing
        /// on one poller never shows here or in another poller built from these options.
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
        /// Path of the Bayeux endpoint, relative to the HTTP client's base address. Message names
        /// are appended to it, producing URIs such as <c>{CometdPath}/connect</c>.
        /// </summary>
        public string CometdPath { get; }

        /// <summary>
        /// Extensions that read and write the <c>ext</c> field of every message, in the order given.
        /// Empty when none were supplied.
        /// </summary>
        public IReadOnlyList<IBayeuxExtension> Extensions { get; }

        /// <summary>
        /// How to re-establish a session that an error ended, or <c>null</c> - the default - for no
        /// reconnection: the first error stops the poller and is reported by the disconnect event.
        /// </summary>
        /// <remarks>
        /// Shared, not copied: it is immutable, so one instance can serve any number of pollers.
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
        /// type metadata in it, so one instance serving every poller is both safe and cheaper.
        /// </remarks>
        public JsonSerializerOptions JsonSerializerOptions { get; }

        /// <summary>Creates options for a poller.</summary>
        /// <param name="channels">
        /// Channels to subscribe to on connect, with their handlers. Copied, so later changes to
        /// the collection you pass have no effect.
        /// </param>
        /// <param name="cometdPath">Path of the Bayeux endpoint.</param>
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
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="extensions"/> contains <c>null</c>.</exception>
        /// <remarks>
        /// One constructor with optional parameters rather than an overload per combination: the
        /// public API analyzer rejects more than one overload with optional parameters (RS0026),
        /// because each further parameter would risk making existing calls ambiguous.
        /// </remarks>
        public PollerOptions(
            IEnumerable<KeyValuePair<string, BayeuxEventHandler>> channels,
            string cometdPath,
            TimeSpan? disconnectTimeout = null,
            IEnumerable<IBayeuxExtension>? extensions = null,
            ReconnectOptions? reconnectOptions = null,
            JsonSerializerOptions? jsonSerializerOptions = null)
        {
            if (channels is null)
                throw new ArgumentNullException(nameof(channels));

            Channels = new ReadOnlyDictionary<string, BayeuxEventHandler>(channels.ToDictionary(pair => pair.Key, pair => pair.Value));
            CometdPath = cometdPath ?? throw new ArgumentNullException(nameof(cometdPath));
            DisconnectTimeout = disconnectTimeout ?? TimeSpan.FromMilliseconds(7000);
            ReconnectOptions = reconnectOptions;
            JsonSerializerOptions = jsonSerializerOptions ?? new(JsonSerializerDefaults.Web);

            var copy = extensions?.ToArray() ?? Array.Empty<IBayeuxExtension>();

            if (copy.Contains(null))
                throw new ArgumentException("Extensions must not contain null.", nameof(extensions));

            Extensions = copy;
        }
    }
}
