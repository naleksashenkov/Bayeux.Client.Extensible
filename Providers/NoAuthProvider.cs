// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>
    /// An <see cref="IAuthProvider"/> that adds nothing to outgoing requests.
    /// </summary>
    /// <remarks>
    /// Use this for servers that need no authentication at all, and for deployments where the
    /// session is established outside the poller &#8212; for example when a cookie obtained by an
    /// earlier login is carried by the shared <see cref="System.Net.CookieContainer"/>, or when a
    /// reverse proxy authenticates the request before the CometD server ever sees it.
    /// </remarks>
    public sealed class NoAuthProvider : IAuthProvider
    {
        /// <summary>
        /// A shared instance. The provider holds no state, so a single instance can serve any
        /// number of pollers.
        /// </summary>
        public static NoAuthProvider Instance { get; } = new NoAuthProvider();

        /// <summary>Does nothing and completes synchronously.</summary>
        /// <param name="request">The request, left unchanged.</param>
        /// <param name="cancellationToken">Ignored; this provider never blocks.</param>
        /// <returns>A completed task.</returns>
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}