// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net.Http.Headers;

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>Sends <c>Authorization: Bearer {token}</c>, for OAuth 2.0 access tokens and the like.</summary>
    /// <remarks>
    /// <para>
    /// The token is whatever the application holds; replacing it before it expires is the
    /// application's job, through <see cref="UpdateCredentials"/> - from a timer, or from
    /// <c>ReconnectOptions.BeforeAttemptAsync</c> after the server refused the old one.
    /// </para>
    /// <para>
    /// Over a WebSocket the token goes out once per socket, in the upgrade request; a replacement
    /// reaches the server with the next socket.
    /// </para>
    /// </remarks>
    public sealed class BearerTokenAuthProvider : IAuthProvider<string>
    {
        // Built once per token, not per request, and swapped whole. volatile makes the swap
        // visible to the thread that sends.
        private volatile AuthenticationHeaderValue _bearerTokenHeader;

        /// <inheritdoc/>
        public event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

        /// <summary>Creates a provider for the given token.</summary>
        /// <param name="bearerToken">The token alone, without the <c>Bearer</c> scheme.</param>
        /// <exception cref="ArgumentException">
        /// The token is empty, or starts with <c>Bearer </c> - the scheme is added here.
        /// </exception>
        public BearerTokenAuthProvider(string bearerToken)
        {
            _bearerTokenHeader = GetTokenHeader(bearerToken);
        }

        /// <summary>Sets the <c>Authorization</c> header to the current token.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="cancellationToken">Ignored; this provider performs no I/O.</param>
        /// <returns>A completed task.</returns>
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = _bearerTokenHeader;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Replaces the token. The next request carries it; requests already in flight keep the old one.
        /// </summary>
        /// <param name="bearerToken">The new token, without the <c>Bearer</c> scheme.</param>
        /// <remarks>
        /// Never throws. A token the constructor would refuse is reported through
        /// <see cref="OnCredentialsUpdated"/>, and the previous one stays in use.
        /// </remarks>
        public void UpdateCredentials(string bearerToken)
        {
            try
            {
                _bearerTokenHeader = GetTokenHeader(bearerToken);
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create());
            }
            catch (Exception ex)
            {
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create(ex));
            }
        }

        // Checked here, for the constructor and UpdateCredentials alike. The scheme check catches
        // the usual mistake - a header value passed as the token - without refusing a token that
        // merely contains the word.
        private AuthenticationHeaderValue GetTokenHeader(string bearerToken)
        {
            if (string.IsNullOrEmpty(bearerToken) || bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Bearer token must be not null or empty. Pass it without \"Bearer\" scheme.",
                    nameof(bearerToken));

            return new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }
}
