// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>
    /// An access token and the moment it stops being valid: what a token source hands to
    /// <see cref="RefreshingTokenAuthProvider"/>.
    /// </summary>
    /// <remarks>
    /// The OAuth access token, not the refresh token some flows use to obtain it: keeping that one
    /// is the token source's business. Immutable, and checked when created.
    /// </remarks>
    public sealed class AccessToken
    {
        /// <summary>The token alone, without the <c>Bearer</c> scheme.</summary>
        public string Token { get; }

        /// <summary>
        /// When the token stops being valid - as identity libraries report it: MSAL's
        /// <c>ExpiresOn</c>, or <c>DateTimeOffset.FromUnixTimeSeconds</c> of a JWT's <c>exp</c>.
        /// </summary>
        public DateTimeOffset ExpiresAt { get; }

        /// <summary>Creates the token.</summary>
        /// <param name="token">The token, without the <c>Bearer</c> scheme.</param>
        /// <param name="expiresAt">When it stops being valid.</param>
        /// <exception cref="ArgumentException">
        /// The token is empty, or starts with <c>Bearer </c> - the scheme is added when it is sent.
        /// </exception>
        public AccessToken(string token, DateTimeOffset expiresAt)
        {
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The token must be not null or empty. Pass it without \"Bearer\" scheme.", nameof(token));

            Token = token;
            ExpiresAt = expiresAt;
        }
    }
}
