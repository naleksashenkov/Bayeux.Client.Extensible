// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net.Http.Headers;
using System.Text;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Authentication.Models;

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>
    /// Sends HTTP Basic credentials on every Bayeux request.
    /// </summary>
    /// <remarks>
    /// Credentials can be replaced at any time through
    /// <see cref="UpdateCredentials(BasicAuthCredentials)"/>; the change applies to the next
    /// request without restarting the CometD session.
    /// </remarks>
    public class HttpBasicAuthProvider : IAuthProvider<BasicAuthCredentials>
    {
        // Written by the caller's thread through UpdateCredentials, read by the polling thread
        // in ApplyAsync. volatile is what makes the replacement visible without a lock.
        private volatile AuthenticationHeaderValue _authHeader;

        /// <inheritdoc/>
        public event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

        /// <summary>Creates a provider for the given credentials.</summary>
        /// <param name="credentials">The user name and password to encode.</param>
        /// <exception cref="ArgumentNullException"><paramref name="credentials"/> is <c>null</c>.</exception>
        public HttpBasicAuthProvider(BasicAuthCredentials credentials)
        {
            _authHeader = GetEncodedValue(credentials);
        }

        /// <summary>
        /// Sets the <c>Authorization</c> header to the current Basic credentials.
        /// </summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="cancellationToken">Ignored; this provider performs no I/O.</param>
        /// <returns>A completed task.</returns>
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = _authHeader;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Replaces the credentials. The next request carries the new value; requests already in
        /// flight keep the old one.
        /// </summary>
        /// <param name="credentials">The new user name and password.</param>
        /// <remarks>
        /// Never throws. Failure to encode the credentials is reported through
        /// <see cref="OnCredentialsUpdated"/>, and the previous credentials remain in use.
        /// </remarks>
        public void UpdateCredentials(BasicAuthCredentials credentials)
        {
            try
            {
                _authHeader = GetEncodedValue(credentials);
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create());
            }
            catch (Exception ex)
            {
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create(ex));
            }
        }

        // RFC 7617 leaves the charset ambiguous; ISO-8859-1 is what servers historically assume
        // and is identical to UTF-8 for ASCII credentials.
        private AuthenticationHeaderValue GetEncodedValue(BasicAuthCredentials credentials)
        {
            return new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(
                    Encoding.GetEncoding("ISO-8859-1")
                    .GetBytes(credentials.Login + ":" + credentials.Password)));
        }
    }
}
