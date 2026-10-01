// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The server refused the handshake: <c>/meta/handshake</c> answered with
    /// <c>successful: false</c>.
    /// </summary>
    /// <remarks>
    /// Usually rejected credentials. <see cref="Reconnect"/> carries the server's advice on whether
    /// trying again can help; CometD sends <c>none</c> when its security policy denies a handshake.
    /// </remarks>
    public sealed class BayeuxHandshakeException : Exception
    {
        /// <summary>The Bayeux error string, such as <c>403::Handshake denied</c>, or <c>null</c>.</summary>
        public string? Error { get; }

        /// <summary>
        /// <c>advice.reconnect</c> from the reply - <c>handshake</c>, <c>retry</c> or <c>none</c> -
        /// or <c>null</c> when the server gave no advice.
        /// </summary>
        public string? Reconnect { get; }

        /// <summary>
        /// The reply's <c>ext</c>, or <c>null</c> when it had none. Where a server puts the details
        /// behind a generic error: Salesforce answers <c>403::Handshake denied</c> and gives the real
        /// cause - a revoked token, a connection limit - under <c>sfdc.failureReason</c>.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement>? Ext { get; }

        /// <summary>Creates the exception from the server's reply.</summary>
        /// <param name="error">The reply's <c>error</c> field.</param>
        /// <param name="reconnect">The reply's <c>advice.reconnect</c>.</param>
        /// <param name="ext">The reply's <c>ext</c>, or <c>null</c>.</param>
        public BayeuxHandshakeException(string? error, string? reconnect, IReadOnlyDictionary<string, JsonElement>? ext = null)
        : base(string.IsNullOrEmpty(error)
            ? "The handshake was refused by server."
            : $"The handshake was refused by server with error {error}")
        {
            Error = error;
            Reconnect = reconnect;
            Ext = ext;
        }
    }
}
