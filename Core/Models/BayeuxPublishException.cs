// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// The server refused a published message: the reply on its channel came back with
    /// <c>successful: false</c>.
    /// </summary>
    /// <remarks>
    /// Concerns that one message only; the session carries on. Typically the account may not
    /// publish to the channel. Nothing was delivered.
    /// </remarks>
    public sealed class BayeuxPublishException : Exception
    {
        /// <summary>The channel the message was published to.</summary>
        public string Channel { get; }

        /// <summary>The Bayeux error string, such as <c>403:/chat/room1:Publish denied</c>, or <c>null</c>.</summary>
        public string? Error { get; }

        /// <summary>The reply's <c>ext</c>, or <c>null</c> when it had none.</summary>
        public IReadOnlyDictionary<string, JsonElement>? Ext { get; }

        /// <summary>Creates the exception from the server's reply.</summary>
        /// <param name="channel">The channel the message was published to.</param>
        /// <param name="error">The reply's <c>error</c> field.</param>
        /// <param name="ext">The reply's <c>ext</c>, or <c>null</c>.</param>
        public BayeuxPublishException(string channel, string? error, IReadOnlyDictionary<string, JsonElement>? ext = null)
        : base(string.IsNullOrEmpty(error)
            ? "The publish was refused by the server"
            : $"The publish was refused by the server with error {error}")
        {
            Channel = channel;
            Error = error;
            Ext = ext;
        }
    }
}
