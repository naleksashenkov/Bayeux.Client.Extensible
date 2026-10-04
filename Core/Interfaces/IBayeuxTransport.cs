// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// How Bayeux messages reach the server and its replies come back: HTTP long-polling, and later
    /// WebSocket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line between the two layers: a transport delivers messages and knows nothing of what they
    /// mean; the client owns the protocol - extensions, the session, events, errors. Nothing here
    /// reads a Bayeux reply.
    /// </para>
    /// <para>
    /// Internal until a caller needs to supply one of their own.
    /// </para>
    /// </remarks>
    internal interface IBayeuxTransport : IAsyncDisposable
    {
        /// <summary>The protocol's name for this transport: <c>long-polling</c> or <c>websocket</c>.</summary>
        string ConnectionType { get; }

        /// <summary>
        /// How long one request may take before the transport itself gives up, or <c>null</c> for no
        /// limit. A held <c>/meta/connect</c> must fit inside it; the client checks that against
        /// the server's advice.
        /// </summary>
        TimeSpan? RequestTimeout { get; }

        /// <summary>Sends the messages as one batch and returns what came back.</summary>
        /// <param name="messages">The messages, already through the outgoing extensions.</param>
        /// <param name="cancellationToken">Cancels the exchange.</param>
        /// <returns>
        /// The replies to the messages - and, for long-polling, any events the server sent with
        /// them. Possibly empty, never null.
        /// </returns>
        Task<BayeuxResponseMessageModel[]> SendAsync(BayeuxRequestModel[] messages, CancellationToken cancellationToken);
    }
}
