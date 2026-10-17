// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using static Bayeux.Client.Extensible.Core.BayeuxConstants;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The timesync extension: how far the server's clock is from this machine's, and how long a
    /// message takes on the network - so that the application can act on the server's time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every handshake and connect carries the client's clock; the server answers with its own, and
    /// with how long it held the message. From that the client takes
    /// <c>lag = (received - sent - held) / 2</c> and <c>offset = server - sent - lag</c>, averaged
    /// over the last samples. Leaving the hold out is what keeps a <c>/meta/connect</c> held for
    /// the server's whole timeout from passing as network delay.
    /// </para>
    /// <para>
    /// Needs the server's side of the extension - CometD's <c>TimesyncExtension</c>. Until the
    /// server has answered, <see cref="IsTimesyncSupported"/> is <c>false</c>, the estimates are
    /// zero, and <see cref="ServerTime"/> is this machine's time.
    /// </para>
    /// <para>
    /// The estimates outlive sessions: the distance between two clocks belongs to the machines, not
    /// to a session.
    /// </para>
    /// </remarks>
    public sealed class BayeuxTimesyncExtension : IBayeuxExtension
    {
        // Lag and offset of the same moment, swapped whole: written by the client's thread, read by
        // the application's, which never pairs a new lag with an old offset. Null until the first
        // answer.
        private volatile Estimate? _estimate;

        private volatile bool _isTimesyncSupported;

        private readonly int _maxSamples;

        // The last samples, oldest first. Under _samplesLock: one sample in, the oldest out, and the
        // averages taken, as one step.
        private readonly Queue<(long Lag, long Offset)> _samples = new();

        private readonly object _samplesLock = new();

        /// <summary>Whether the server has answered with timesync at least once.</summary>
        public bool IsTimesyncSupported => _isTimesyncSupported;

        /// <summary>
        /// The server's clock minus this machine's, averaged over the last samples; zero until the
        /// server has answered.
        /// </summary>
        public TimeSpan Offset => _estimate?.Offset ?? TimeSpan.Zero;

        /// <summary>The time a message takes one way on the network, averaged over the last samples.</summary>
        public TimeSpan Lag => _estimate?.Lag ?? TimeSpan.Zero;

        /// <summary>Now, by the server's clock: this machine's time corrected by <see cref="Offset"/>.</summary>
        public DateTimeOffset ServerTime => DateTimeOffset.UtcNow + Offset;

        /// <summary>Creates the extension.</summary>
        /// <param name="maxSamples">
        /// How many of the latest samples the estimates average; ten by default. One sample is noisy:
        /// the network is faster one moment, slower the next.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSamples"/> is less than one.</exception>
        public BayeuxTimesyncExtension(int maxSamples = 10)
        {
            if (maxSamples < 1)
                throw new ArgumentOutOfRangeException(nameof(maxSamples), "Maximum number of samples must be greater than zero.");

            _maxSamples = maxSamples;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Adds the client's clock to every handshake and connect, with the current estimates. Other
        /// messages are left as they are: the server answers timesync on those two only.
        /// </remarks>
        public Task OutgoingAsync(BayeuxRequestModel bayeuxRequest, CancellationToken cancellationToken)
        {
            if (bayeuxRequest is HandshakeRequestModel or ConnectRequestModel)
            {
                var estimate = _estimate;
                Ext(bayeuxRequest)["timesync"] = new Dictionary<string, object>
                {
                    ["tc"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["l"] = (long)(estimate?.Lag.TotalMilliseconds ?? 0),
                    ["o"] = (long)(estimate?.Offset.TotalMilliseconds ?? 0)
                };
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Takes a sample from every handshake and connect reply that carries one. A reply it cannot
        /// read is skipped rather than reported: an extension must not break a session. A sample
        /// with a negative lag - this machine's clock stepped back between send and receive - is
        /// dropped, so as not to skew the average.
        /// </remarks>
        public void Incoming(BayeuxResponseMessageModel bayeuxResponse)
        {
            if (bayeuxResponse.Channel is not (MetaChannels.Handshake or MetaChannels.Connect)
                || bayeuxResponse.Ext is null
                || !bayeuxResponse.Ext.TryGetValue("timesync", out var timesync)
                || !TryRead(timesync, out var tc, out var ts, out var p))
                return;

            // One moment for the whole calculation.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var lag = (now - tc - p) / 2;

            if (lag < 0)
                return;

            var offset = ts - tc - lag;

            lock (_samplesLock)
            {
                _samples.Enqueue((lag, offset));

                if (_samples.Count > _maxSamples)
                    _samples.Dequeue();

                _estimate = new Estimate(
                    TimeSpan.FromMilliseconds(_samples.Average(sample => sample.Lag)),
                    TimeSpan.FromMilliseconds(_samples.Average(sample => sample.Offset)));
            }

            _isTimesyncSupported = true;
        }

        // The server's answer: { "tc": sent, "ts": server time on receipt, "p": held for, "a": accuracy }.
        // "a" is the server's own business and not needed here.
        private static bool TryRead(JsonElement timesync, out long tc, out long ts, out long p)
        {
            tc = ts = p = 0;

            return timesync.ValueKind == JsonValueKind.Object
                && TryGetLong(timesync, "tc", out tc)
                && TryGetLong(timesync, "ts", out ts)
                && TryGetLong(timesync, "p", out p);
        }

        // TryGetInt64 throws on anything but a number: the kind is checked first.
        private static bool TryGetLong(JsonElement element, string name, out long value)
        {
            value = 0;

            return element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out value);
        }

        // The message's ext, created when this is the first extension to write to it. Never
        // replaced: another extension - acknowledgements - may already have put its own keys there.
        private static Dictionary<string, object> Ext(BayeuxRequestModel requestModel) =>
            requestModel.Ext ??= new Dictionary<string, object>();

        // One estimate, immutable.
        private sealed class Estimate
        {
            public TimeSpan Lag { get; }

            public TimeSpan Offset { get; }

            public Estimate(TimeSpan lag, TimeSpan offset)
            {
                Lag = lag;
                Offset = offset;
            }
        }
    }
}
