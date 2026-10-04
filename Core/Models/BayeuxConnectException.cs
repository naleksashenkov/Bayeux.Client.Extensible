// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The server refused a <c>/meta/connect</c>: it answered with <c>successful: false</c> and
    /// advice not to reconnect, and has dropped the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refusal, not a goodbye. A server that simply ends a session answers successfully, or sends
    /// <c>/meta/disconnect</c>; that stops the client with
    /// <see cref="DisconnectReason.ServerRequirement"/> and no error. This exception stops it with
    /// <see cref="DisconnectReason.Failed"/>, so the cause reaches the caller and a reconnect
    /// policy can decide what to do about it.
    /// </para>
    /// <para>
    /// Salesforce, for one, refuses with <c>401::Authentication invalid</c> when an access token is
    /// revoked, and expects the client to authenticate again and reconnect.
    /// </para>
    /// </remarks>
    public sealed class BayeuxConnectException : Exception
    {
        /// <summary>The Bayeux error string, such as <c>401::Authentication invalid</c>, or <c>null</c>.</summary>
        public string? Error { get; }

        /// <summary>
        /// <c>advice.reconnect</c> from the reply - <c>none</c> for every refusal the client raises
        /// this for - or <c>null</c> when the server gave no advice.
        /// </summary>
        public string? Reconnect { get; }

        /// <summary>
        /// The reply's <c>ext</c>, or <c>null</c> when it had none. Where a server puts the details:
        /// Salesforce, for example, the real cause under <c>sfdc.failureReason</c>.
        /// </summary>
        public IReadOnlyDictionary<string, JsonElement>? Ext { get; }

        /// <summary>Creates the exception from the server's reply.</summary>
        /// <param name="error">The reply's <c>error</c> field.</param>
        /// <param name="reconnect">The reply's <c>advice.reconnect</c>.</param>
        /// <param name="ext">The reply's <c>ext</c>, or <c>null</c>.</param>
        public BayeuxConnectException(string? error, string? reconnect, IReadOnlyDictionary<string, JsonElement>? ext = null)
        : base(string.IsNullOrEmpty(error)
            ? "The connect was refused by server."
            : $"The connect was refused by server with error {error}")
        {
            Error = error;
            Reconnect = reconnect;
            Ext = ext;
        }
    }
}
