// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>Sends an API key in a request header of its own, <c>X-API-Key</c> by default.</summary>
    /// <remarks>
    /// Over a WebSocket the key goes out once per socket, in the upgrade request; a replacement
    /// reaches the server with the next socket.
    /// </remarks>
    public sealed class ApiKeyAuthProvider : IAuthProvider<ApiKeyCredentials>
    {
        // One immutable object, swapped whole: the key and its header name always change together,
        // and ApplyAsync can never pair a new key with an old name. volatile makes the swap visible
        // to the thread that sends.
        private volatile ApiKeyCredentials _credentials;

        /// <inheritdoc/>
        public event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

        /// <summary>Creates a provider for the given key.</summary>
        /// <param name="credentials">The key and its header.</param>
        /// <exception cref="ArgumentNullException"><paramref name="credentials"/> is <c>null</c>.</exception>
        public ApiKeyAuthProvider(ApiKeyCredentials credentials)
        {
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        }

        /// <summary>Adds the key in its header.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="cancellationToken">Ignored; this provider performs no I/O.</param>
        /// <returns>A completed task.</returns>
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read once: the name and the key below belong to the same credentials.
            var currentCredentials = _credentials;

            // The result is not checked: the name was checked when the credentials were created,
            // and they cannot change since.
            request.Headers.TryAddWithoutValidation(currentCredentials.HeaderName, currentCredentials.ApiKey);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Replaces the key. The next request carries it; requests already in flight keep the old one.
        /// </summary>
        /// <param name="credentials">The new key and its header.</param>
        /// <remarks>
        /// Never throws. <c>null</c> is reported through <see cref="OnCredentialsUpdated"/> and the
        /// previous key stays in use; anything else was checked when it was created.
        /// </remarks>
        public void UpdateCredentials(ApiKeyCredentials credentials)
        {
            try
            {
                _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create());
            }
            catch (Exception ex)
            {
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create(ex));
            }
        }
    }
}
