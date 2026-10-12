// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The server answered the handshake but does not offer the transport the client came over.
    /// </summary>
    /// <remarks>
    /// Internal: with <see cref="BayeuxTransportType.Auto"/> it is the signal to fall back to
    /// long-polling and never leaves the client. Otherwise it is reported as
    /// <see cref="ErrorSource.Configuration"/> and reaches the caller as the
    /// <see cref="InvalidOperationException"/> it derives from - the same type as before it existed.
    /// </remarks>
    internal sealed class BayeuxTransportNotSupportedException : InvalidOperationException
    {
        /// <summary>The transport the client used: <c>websocket</c> or <c>long-polling</c>.</summary>
        public string ConnectionType { get; }

        /// <summary>What the server offers instead.</summary>
        public IReadOnlyList<string> SupportedConnectionTypes { get; }

        /// <summary>Creates the exception.</summary>
        /// <param name="connectionType">The transport the client used.</param>
        /// <param name="supportedConnectionTypes">The server's <c>supportedConnectionTypes</c>.</param>
        public BayeuxTransportNotSupportedException(string connectionType, IReadOnlyList<string> supportedConnectionTypes)
        : base($"The server does not support selected connection type {connectionType}. " +
            $"Supported types: {string.Join(", ", supportedConnectionTypes)}")
        {
            ConnectionType = connectionType; 
            SupportedConnectionTypes = supportedConnectionTypes;
        }
    }
}