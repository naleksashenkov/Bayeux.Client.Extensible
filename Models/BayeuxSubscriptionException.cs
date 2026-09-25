// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core.Models
{
    /// <summary>
    /// Thrown when the server answered a batch subscribe or unsubscribe and accepted only part of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The channels in <see cref="Succeeded"/> are in the state the call asked for. The channels in
    /// <see cref="Failures"/> are not, and have already been rolled back locally, so the poller's
    /// own view still matches the server's.
    /// </para>
    /// <para>
    /// A transport failure does not produce this exception. There the outcome is unknown, so the
    /// whole batch is rolled back and the original error propagates unchanged &#8212; the absence of
    /// this type is what tells the caller that nothing was applied.
    /// </para>
    /// <para>
    /// Retrying just the failed channels is safe: Bayeux accepts a repeated subscribe to a channel
    /// that is already subscribed, and a repeated unsubscribe from one that is not.
    /// </para>
    /// </remarks>
    public sealed class BayeuxSubscriptionException : Exception
    {
        /// <summary>The channels the server rejected, each with the reason it gave.</summary>
        public IReadOnlyDictionary<string, Exception> Failures { get; }

        /// <summary>The channels the server accepted. These are the ones that did change state.</summary>
        public IReadOnlyList<string> Succeeded { get; }

        /// <summary>Creates the exception describing the outcome of one batch.</summary>
        /// <param name="failures">The rejected channels and the reasons the server gave.</param>
        /// <param name="succeeded">The channels the server accepted.</param>
        public BayeuxSubscriptionException(IReadOnlyDictionary<string, Exception> failures, IReadOnlyList<string> succeeded)
        : base($"{failures.Count} of {failures.Count + succeeded.Count} channels were rejected: "
           + string.Join(", ", failures.Keys))
        {
            Failures = failures;
            Succeeded = succeeded;
        }
    }
}
