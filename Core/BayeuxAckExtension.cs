// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The acknowledge extension: the server keeps the events it sends until the client confirms
    /// them, and sends them again if the reply carrying them was lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server numbers each <c>/meta/connect</c> reply that carries events - a batch - and the
    /// next connect tells it the last batch received. Until then the events stay queued. A reply
    /// lost to a network failure is therefore not lost: with a reconnect policy the poller retries
    /// <c>/meta/connect</c> in the same session, still confirming the earlier batch, and the server
    /// sends the rest again.
    /// </para>
    /// <para>
    /// <b>Acknowledged means handled.</b> The poller sends the next connect only after every handler
    /// for the previous events has returned, so the server discards nothing a handler has not
    /// finished with.
    /// </para>
    /// <para>
    /// <b>At-least-once within a session.</b> An event may arrive twice - its reply arrived, but
    /// the confirmation did not - so handlers must tolerate duplicates. A session that ends, by
    /// expiry or a new handshake, still loses its queue: batches belong to a session.
    /// </para>
    /// <para>
    /// A server with acknowledgements on delivers events in <c>/meta/connect</c> replies only,
    /// never in the replies to subscribe or publish.
    /// </para>
    /// </remarks>
    public sealed class BayeuxAckExtension : IBayeuxExtension
    {
        // Set from the handshake reply, read when each connect goes out: written on the thread that
        // called ConnectAsync, read on the loop's. Volatile, so the loop sees the write.
        private volatile bool _serverSupportsAck;

        // The last batch the server sent. A long is written in two halves in a 32-bit .NET
        // Framework process, so a plain read could see half of an old value and half of a new one;
        // Interlocked reads and writes it whole.
        private long _batch;

        /// <summary>Whether the server agreed to acknowledgements in the last handshake.</summary>
        public bool IsAckSupported => _serverSupportsAck;

        /// <inheritdoc/>
        /// <remarks>
        /// Asks for acknowledgements in every handshake, and confirms the last batch in every
        /// connect once the server has agreed. A connect to a server that has not agreed is left as is.
        /// </remarks>
        public Task OutgoingAsync(BaseLongPollingRequestModel requestModel, CancellationToken cancellationToken)
        {
            if (requestModel is HandshakeRequestModel)
            {
                // A new session starts from nothing: batches of the previous one mean nothing here.
                _serverSupportsAck = false;
                Interlocked.Exchange(ref _batch, 0);

                Ext(requestModel)["ack"] = true;
            }
            else if (requestModel is ConnectRequestModel && _serverSupportsAck)
                Ext(requestModel)["ack"] = Interlocked.Read(ref _batch);

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Reads the server's agreement from the handshake reply - <c>true</c>, or
        /// <c>{ "enabled": true, "batch": n }</c> from newer servers - and the batch number from each
        /// successful connect reply. Runs before the handlers, but the number goes back to the
        /// server only with the next connect, after they have finished.
        /// </remarks>
        public void Incoming(BayeuxResponseMessageModel responseModel)
        {
            if (responseModel.Ext is null || !responseModel.Ext.TryGetValue("ack", out var ack))
                return;
            
            if (responseModel.Channel == CometDConstants.MetaChannels.Handshake)
            {
                switch (ack.ValueKind)
                {
                    case JsonValueKind.True:
                        _serverSupportsAck = true;
                        break;
                    case JsonValueKind.Object:
                        _serverSupportsAck = ack.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
                        if (ack.TryGetProperty("batch", out var batch) && batch.TryGetInt64(out var batchValue))
                            Interlocked.Exchange(ref _batch, batchValue);
                        break;
                    default:
                        _serverSupportsAck = false;
                        break;
                }
            }
            // Only a successful reply numbers a batch; a failed one carries no events to confirm.
            else if (responseModel.Channel == CometDConstants.MetaChannels.Connect &&
                responseModel.IsSuccessful == true &&
                _serverSupportsAck &&
                ack.TryGetInt64(out var batchValue))
                Interlocked.Exchange(ref _batch, batchValue);
        }

        // The message's ext, created when this is the first extension to write to it. Never
        // replaced: another extension may already have put its own keys there.
        private static Dictionary<string, object> Ext(BaseLongPollingRequestModel requestModel) =>
            requestModel.Ext ??= new Dictionary<string, object>();
    }
}