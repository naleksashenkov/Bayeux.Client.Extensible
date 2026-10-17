// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net.Http.Headers;

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>
    /// Sends a bearer token it obtains and renews itself, from a source you supply - MSAL, an
    /// OAuth client, your own token endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is fetched on the first request and renewed ahead of its expiry, on a request,
    /// without a timer of its own: over long-polling every <c>/meta/connect</c> is one. However many
    /// requests find it due at once, the source is called once and the others take its result.
    /// </para>
    /// <para>
    /// Two moments matter. From <c>RefreshAt</c> - <c>refreshBefore</c> ahead of expiry, or at half
    /// the token's life if that is shorter - a renewal is tried; if it fails, the token in hand,
    /// still valid, is sent and the failure reported through <see cref="OnCredentialsUpdated"/>. From
    /// <c>ExpiresAt</c> nothing can be sent: a failure to renew fails the request, and the client's
    /// reconnect policy decides what follows.
    /// </para>
    /// <para>
    /// A token the server refuses before its time - Salesforce's <c>401::Authentication invalid</c>
    /// - is dropped with <see cref="Invalidate"/>, and the next request fetches a new one.
    /// </para>
    /// <para>
    /// Over a WebSocket the token goes out once per socket, in the upgrade request: a socket
    /// outlives its token unless the server closes it, and the next socket carries a new one.
    /// </para>
    /// </remarks>
    public sealed class RefreshingTokenAuthProvider : IAuthProvider
    {
        private readonly Func<CancellationToken, Task<AccessToken>> _getFreshToken;

        private readonly TimeSpan _refreshBefore;

        // One fetch at a time. A SemaphoreSlim, not a lock: the fetch is awaited inside it.
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        // The token in use, or null before the first fetch and after Invalidate. Immutable, swapped
        // whole, and read once per request, so one request never mixes two tokens' values.
        private volatile CachedAccessToken? _cachedToken;

        /// <summary>
        /// Raised after every fetch: success, or the exception the token source threw. A fetch
        /// called off by the request's own cancellation is not reported.
        /// </summary>
        public event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

        /// <summary>Creates the provider. Nothing is fetched until the first request.</summary>
        /// <param name="getFreshToken">
        /// Obtains a new token. Called on a request, so it should finish promptly and honour the
        /// token; it may throw, and what it throws reaches the request.
        /// </param>
        /// <param name="refreshBefore">
        /// How long before expiry to renew; a minute by default, and never more than half the
        /// token's life.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="getFreshToken"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="refreshBefore"/> is negative.</exception>
        public RefreshingTokenAuthProvider(
            Func<CancellationToken, Task<AccessToken>> getFreshToken,
            TimeSpan? refreshBefore = null)
        {
            if (refreshBefore < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(
                    nameof(refreshBefore),
                    "The refresh timing must be equal or greater than zero.");

            _getFreshToken = getFreshToken ?? throw new ArgumentNullException(
                nameof(getFreshToken),
                "The function for getting fresh token can not be null");
            _refreshBefore = refreshBefore ?? TimeSpan.FromMinutes(1);
        }

        /// <summary>Sets the <c>Authorization</c> header, fetching or renewing the token first when due.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="cancellationToken">Cancels this request's wait for a token, not anyone else's.</param>
        /// <returns>A task that completes once the header is set.</returns>
        /// <exception cref="Exception">Whatever the token source threw, when there is no valid token to send instead.</exception>
        public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var capturedToken = _cachedToken;
            var now = DateTimeOffset.UtcNow;

            // None, or expired: nothing to send without a new one - a failure goes to the caller.
            if (capturedToken == null || capturedToken.ExpiresAt <= now)
                capturedToken = await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
            // Due but still valid: a failure to renew must not fail the request. The event has
            // reported it; the token in hand goes out.
            else if (capturedToken.RefreshAt <= now)
            {
                try
                {
                    capturedToken = await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            }

            request.Headers.Authorization = capturedToken.AccessTokenHeader;
        }

        /// <summary>
        /// Drops the token in use, so that the next request fetches a new one - for a token the
        /// server refused before its time.
        /// </summary>
        /// <remarks>
        /// Typically from <c>OnError</c>, on a <c>401</c> or Salesforce's
        /// <c>401::Authentication invalid</c>, with a reconnect policy that retries it.
        /// </remarks>
        public void Invalidate() => _cachedToken = null;

        private async Task<CachedAccessToken> RefreshTokenAsync(CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // The question that brought us here, asked again: is a renewal still due? Another
                // caller may have renewed while this one waited - theirs is good until its RefreshAt.
                // If they failed, the old token is still due, and this caller tries itself. Asking
                // about ExpiresAt instead would return the old token every time before expiry, and
                // renewal ahead of it would never happen.
                if (_cachedToken is { } fresh && fresh.RefreshAt > DateTimeOffset.UtcNow)
                    return fresh;

                AccessToken freshToken;

                try
                {
                    freshToken = await _getFreshToken(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The refreshing operation returned null.");
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create(ex));
                    throw;
                }

                var cachedToken = CacheToken(freshToken);
                _cachedToken = cachedToken;
                OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create());
                return cachedToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private CachedAccessToken CacheToken(AccessToken accessToken)
        {
            // One moment for both checks below: read twice, the token could expire in between and
            // its renewal time land after its expiry.
            var currentTime = DateTimeOffset.UtcNow;

            if (accessToken.ExpiresAt <= currentTime)
                throw new InvalidOperationException("The token source returned a token that is already expired.");

            // Never more than half the token's life: a 30-second token with a minute's margin would
            // otherwise be due from its first second, and every request would fetch a new one.
            var margin = TimeSpan.FromTicks(Math.Min(
                _refreshBefore.Ticks, 
                (accessToken.ExpiresAt - currentTime).Ticks / 2));

            return new CachedAccessToken(
                accessToken.Token, 
                accessToken.ExpiresAt,
                accessToken.ExpiresAt - margin);
        }

        // A token as sent: its header built once, and the two moments that govern it.
        private sealed class CachedAccessToken
        {
            internal readonly AuthenticationHeaderValue AccessTokenHeader;

            internal readonly DateTimeOffset ExpiresAt;

            internal readonly DateTimeOffset RefreshAt;

            public CachedAccessToken(string accessToken, DateTimeOffset expiresAt, DateTimeOffset refreshAt)
            {
                AccessTokenHeader = new AuthenticationHeaderValue("Bearer", accessToken);
                ExpiresAt = expiresAt;
                RefreshAt = refreshAt;
            }
        }
    }
}
