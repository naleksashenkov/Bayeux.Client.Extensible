// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Interfaces;

namespace Bayeux.Client.Extensible.Core.Models
{
    /// <summary>Configuration for a <see cref="CometDPoller"/>.</summary>
    public class CometdPollerOptions
    {
        // Copied from the caller's collection rather than aliased: keeping their instance would
        // leave a reference through which channels could be added that the poller never subscribes.
        private readonly ConcurrentDictionary<string, BayeuxEventHandler> _channels;

        /// <summary>
        /// The channels currently subscribed, each with the handler invoked for its messages.
        /// Re-subscribed automatically after every handshake, so the set survives a reconnect.
        /// </summary>
        /// <remarks>
        /// Keys may be exact channel names or Bayeux patterns: <c>/a/*</c> matches one further
        /// segment, <c>/a/**</c> any depth below. Overlapping patterns each receive the message, so
        /// registering both <c>/a/**</c> and <c>/a/b</c> means two handler calls for one message.
        /// <para>
        /// Read-only by design. Channels are added and removed through
        /// <c>SubscribeNewChannelsAsync</c> and <c>UnsubscribeChannelsAsync</c>, which also tell the
        /// server; a bare dictionary entry would never be subscribed.
        /// </para>
        /// </remarks>
        public IReadOnlyDictionary<string, BayeuxEventHandler> Channels => _channels;

        /// <summary>The same set, writable, for the poller to maintain as subscriptions change.</summary>
        internal ConcurrentDictionary<string, BayeuxEventHandler> InternalChannels => _channels;

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
        public IReadOnlyList<IBayeuxExt> Extensions { get; }

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
        /// <exception cref="ArgumentNullException">A required argument is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="extensions"/> contains <c>null</c>.</exception>
        /// <remarks>
        /// One constructor with optional parameters rather than an overload per combination: the
        /// public API analyzer rejects more than one overload with optional parameters (RS0026),
        /// because each further parameter would risk making existing calls ambiguous.
        /// </remarks>
        public CometdPollerOptions(
            IEnumerable<KeyValuePair<string, BayeuxEventHandler>> channels,
            string cometdPath,
            TimeSpan? disconnectTimeout = null,
            IEnumerable<IBayeuxExt>? extensions = null)
        {
            if (channels is null)
                throw new ArgumentNullException(nameof(channels));

            _channels = new ConcurrentDictionary<string, BayeuxEventHandler>(channels);
            CometdPath = cometdPath ?? throw new ArgumentNullException(nameof(cometdPath));
            DisconnectTimeout = disconnectTimeout ?? TimeSpan.FromMilliseconds(7000);

            var copy = extensions?.ToArray() ?? Array.Empty<IBayeuxExt>();

            if (copy.Contains(null))
                throw new ArgumentException("Extensions must not contain null.", nameof(extensions));

            Extensions = copy;
        }
    }
}
