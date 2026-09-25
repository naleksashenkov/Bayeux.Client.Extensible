// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Authentication.EventModels;

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>
    /// Forms credentials for outgoing Bayeux requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations never observe responses. Recognising that a request was rejected is the
    /// poller's job; knowing how to produce credentials is this one's.
    /// </para>
    /// <para>
    /// An implementation must support replacing its credentials in place and must be safe to use
    /// from several threads: the poller resolves the provider once and holds that instance for its
    /// lifetime, so a provider that requires reconstruction cannot rotate credentials without
    /// tearing down the CometD session. Any field read by <see cref="ApplyAsync"/> and written by
    /// callers must therefore be declared <c>volatile</c>.
    /// </para>
    /// </remarks>
    public interface IAuthProvider
    {
        /// <summary>
        /// Decorates an outgoing request. Called once before every Bayeux message, including the
        /// initial handshake and every retry.
        /// </summary>
        /// <param name="request">The request to decorate. Headers may be added or replaced.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <remarks>
        /// This runs on the polling loop's hot path. Implementations should read cached state and
        /// return <see cref="Task.CompletedTask"/>; avoid network I/O unless credentials genuinely
        /// have to be acquired on first use.
        /// </remarks>
        Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// An <see cref="IAuthProvider"/> whose credentials can be replaced at runtime without
    /// recreating the provider or restarting the poller.
    /// </summary>
    /// <typeparam name="TAuthData">The credential type this provider accepts.</typeparam>
    /// <remarks>
    /// The poller only ever depends on the non-generic <see cref="IAuthProvider"/>. This interface
    /// exists for the caller, which knows the concrete credential type and holds the provider
    /// instance it passed to the poller.
    /// </remarks>
    public interface IAuthProvider<TAuthData> : IAuthProvider
    {
        /// <summary>
        /// Raised after <see cref="UpdateCredentials"/> completes, reporting whether the new
        /// credentials were accepted by the provider.
        /// </summary>
        /// <remarks>
        /// This reports only local encoding of the credentials. It says nothing about whether the
        /// server will accept them &#8212; that is only known when the next request is answered.
        /// </remarks>
        event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

        /// <summary>
        /// Replaces the credentials used by subsequent requests. Safe to call while the poller is
        /// running; the change takes effect on the next request with no reconnect.
        /// </summary>
        /// <param name="credentials">The new credentials.</param>
        void UpdateCredentials(TAuthData credentials);
    }
}
