// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>A provider of your own in one lambda, without a class.</summary>
    /// <remarks>
    /// <para>
    /// For a header the built-in providers do not set, or for several providers at once - one
    /// lambda that applies each in turn:
    /// <code>
    /// new DelegateAuthProvider(async (request, ct) =>
    /// {
    ///     await gatewayKey.ApplyAsync(request, ct);
    ///     await userToken.ApplyAsync(request, ct);
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// The lambda runs before every request, on the client's thread, not the caller's: whatever
    /// it reads that the application replaces must be safe to read from another thread - a
    /// <c>volatile</c> field, or an immutable object swapped whole. What it throws reaches the
    /// request, and the client's reconnect policy decides what follows.
    /// </para>
    /// </remarks>
    public sealed class DelegateAuthProvider : IAuthProvider
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task> _apply;

        /// <summary>Creates the provider from an asynchronous lambda, for one that awaits something.</summary>
        /// <param name="apply">Decorates a request; pass the token on to whatever it awaits.</param>
        /// <exception cref="ArgumentNullException"><paramref name="apply"/> is <c>null</c>.</exception>
        public DelegateAuthProvider(Func<HttpRequestMessage, CancellationToken, Task> apply)
        {
            _apply = apply ?? throw new ArgumentNullException(
                nameof(apply), 
                "The lambda must not be null.");
        }

        /// <summary>Creates the provider from a plain lambda - the usual case, setting a header.</summary>
        /// <param name="apply">Decorates a request.</param>
        /// <exception cref="ArgumentNullException"><paramref name="apply"/> is <c>null</c>.</exception>
        /// <remarks>
        /// No ambiguity with the other constructor: a lambda of one parameter fits only this one, a
        /// lambda of two only that one.
        /// </remarks>
        public DelegateAuthProvider(Action<HttpRequestMessage> apply)
        {
            if (apply is null)
                throw new ArgumentNullException(
                    nameof(apply), 
                    "The lambda must not be null.");

            _apply = (request, _) =>
            {
                apply(request);
                return Task.CompletedTask;
            };
        }

        /// <summary>Runs the lambda.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="cancellationToken">Passed to the lambda.</param>
        /// <returns>The lambda's task.</returns>
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _apply(request, cancellationToken);
    }
}
