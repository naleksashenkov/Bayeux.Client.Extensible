// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Opt-in reconnection: what the client does when an error ends an established session.
    /// Without it, the first error stops the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After a failure the client waits, then tries again. A transport failure - no answer, a
    /// timeout, an HTTP error - leaves the session alive on the server for a while, with the events
    /// published meanwhile queued in it, so the client first retries <c>/meta/connect</c> in the same
    /// session. A refusal ends the session, and the client handshakes again and resubscribes every
    /// channel. Attempt <c>n</c> waits a random time between zero and
    /// <c>min(MaxDelay, InitialDelay × Multiplier^(n-1))</c>: the bound grows so that a server
    /// that is down is not hammered, and the randomness keeps clients dropped together by one
    /// restart from all coming back in the same instant. The count starts again once a
    /// <c>/meta/connect</c> succeeds.
    /// </para>
    /// <para>
    /// Only a running session is retried. A failed <c>ConnectAsync</c> still throws, so a wrong
    /// address or password surfaces at once instead of as an endless series of attempts.
    /// </para>
    /// <para>
    /// <c>DisconnectAsync</c> and <c>DisposeAsync</c> interrupt a pending delay, and nothing is
    /// attempted after they return. After a refusal there is no session meanwhile: <c>ClientId</c>
    /// is empty, a subscribe or unsubscribe fails at once, and a stop sends nothing. After a
    /// transport failure the session is kept: <c>ClientId</c> stays, and a stop closes the session
    /// as usual - which, if the server is still unreachable, waits up to
    /// <see cref="BayeuxClientOptions.DisconnectTimeout"/>.
    /// </para>
    /// </remarks>
    public sealed class ReconnectOptions
    {
        /// <summary>Upper bound of the first delay. One second by default.</summary>
        public TimeSpan InitialDelay { get; }

        /// <summary>
        /// Cap on every delay, however many attempts have failed. Thirty seconds by default, or
        /// <see cref="InitialDelay"/> when that is longer.
        /// </summary>
        public TimeSpan MaxDelay { get; }

        /// <summary>
        /// Growth of the bound after each failed attempt. 2 by default; 1 keeps it constant.
        /// </summary>
        public double Multiplier { get; }

        /// <summary>
        /// Failed attempts in a row before the client gives up, or <c>null</c> to keep trying
        /// until it is stopped. <c>null</c> by default: errors that cannot fix themselves are
        /// excluded by <see cref="ShouldRetry"/>, not by a count.
        /// </summary>
        public int? MaxAttempts { get; }

        /// <summary>
        /// Decides whether an error is worth another attempt. <see cref="IsRetriable"/> by default.
        /// </summary>
        /// <remarks>
        /// Runs on the connect loop for every failure, so it should be quick. A predicate that
        /// throws counts as <c>false</c>, and the exception is logged. The client's own stop never
        /// reaches it.
        /// </remarks>
        public Func<Exception, bool> ShouldRetry { get; }

        /// <summary>
        /// Runs before every attempt the client makes on its own - a connect retried in the same
        /// session, or a new handshake after an error or when the server invalidated the session -
        /// typically to fetch fresh credentials. <c>null</c> by default.
        /// </summary>
        /// <remarks>
        /// The token is cancelled when the client stops. An exception counts as a failed attempt
        /// and goes through <see cref="ShouldRetry"/> like any other.
        /// </remarks>
        public Func<CancellationToken, Task>? BeforeAttemptAsync { get; }

        /// <summary>Creates reconnect settings; an argument left out takes its default.</summary>
        /// <param name="initialDelay">Upper bound of the first delay; must be positive.</param>
        /// <param name="maxDelay">Cap on every delay; must not be shorter than the first.</param>
        /// <param name="multiplier">Growth of the bound per failed attempt; 1 or greater.</param>
        /// <param name="maxAttempts">Failed attempts before giving up, or <c>null</c> for no limit.</param>
        /// <param name="shouldRetry">Which errors to retry, or <c>null</c> for <see cref="IsRetriable"/>.</param>
        /// <param name="beforeAttemptAsync">Work to do before each attempt, or <c>null</c>.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is outside its range.</exception>
        /// <remarks>
        /// One constructor with optional parameters rather than overloads: the public API analyzer
        /// allows only one overload with optional parameters (RS0026).
        /// </remarks>
        public ReconnectOptions(
            TimeSpan? initialDelay = null,
            TimeSpan? maxDelay = null,
            double multiplier = 2,
            int? maxAttempts = null,
            Func<Exception, bool>? shouldRetry = null,
            Func<CancellationToken, Task>? beforeAttemptAsync = null)
        {
            InitialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
            // Never below the first delay, so a long initialDelay alone does not fail the check below.
            MaxDelay = maxDelay ?? TimeSpan.FromSeconds(Math.Max(TimeSpan.FromSeconds(30).TotalSeconds, InitialDelay.TotalSeconds));
            Multiplier = multiplier;
            MaxAttempts = maxAttempts;
            ShouldRetry = shouldRetry ?? IsRetriable;
            BeforeAttemptAsync = beforeAttemptAsync;

            if (InitialDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(initialDelay), "Value must be greater than 0");

            if (MaxDelay < InitialDelay)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), $"Value must be not less than provided {nameof(initialDelay)}");

            // Written this way round so that NaN is rejected too: every comparison with NaN is false.
            if (!(multiplier >= 1))
                throw new ArgumentOutOfRangeException(nameof(multiplier), "Value must be 1 or greater");

            if (MaxAttempts <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Value must be null or greater than 0");
        }

        /// <summary>
        /// The default for <see cref="ShouldRetry"/>: <c>true</c> only for errors that can go away
        /// by themselves.
        /// </summary>
        /// <param name="ex">The error that ended the attempt.</param>
        /// <returns>Whether waiting and trying again can help.</returns>
        /// <remarks>
        /// Retried: no response at all (refused, reset, DNS), a timeout, HTTP 5xx, 408 and 429, and
        /// a refused handshake or connect whose advice invites another one. Not retried: any other
        /// HTTP status - 401 and 403 above all, where repeating a rejected login can lock the
        /// account - a handshake or connect refused with advice <c>none</c> or none at all, and
        /// anything unrecognised. Public so that a custom predicate can extend it rather than
        /// replace it, as a Salesforce client does for a revoked token it replaces before each
        /// attempt:
        /// <code>
        /// shouldRetry: e => e is BayeuxConnectException { Error: "401::Authentication invalid" }
        ///                || ReconnectOptions.IsRetriable(e)
        /// </code>
        /// </remarks>
        public static bool IsRetriable(Exception ex) => ex switch
        {
            BayeuxHandshakeException handshake =>
                handshake.Reconnect == "handshake"
                || handshake.Reconnect == "retry",
            BayeuxHttpException http =>
                (int)http.StatusCode >= 500
                || (int)http.StatusCode == 429
                || http.StatusCode == HttpStatusCode.RequestTimeout,
            HttpRequestException => true,
            OperationCanceledException => true,
            BayeuxConnectException connect => connect.Reconnect == "handshake" || connect.Reconnect == "retry",
            _ => false,
        };

        /// <summary>The upper bound of the delay before attempt <paramref name="attempt"/>, scaled by <paramref name="randDouble"/>.</summary>
        /// <param name="attempt">The attempt about to be made, counted from 1.</param>
        /// <param name="randDouble">
        /// A value in [0, 1) from the caller's <see cref="Random"/>. Passed in rather than drawn
        /// here, so the calculation stays pure and a test can pass fixed values.
        /// </param>
        /// <remarks>
        /// <c>Math.Pow</c> reaches infinity for a large attempt number; <c>Math.Min</c> then returns
        /// <see cref="MaxDelay"/>, so the result stays finite.
        /// </remarks>
        internal TimeSpan GetReconnectDelay(int attempt, double randDouble) =>
        TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, InitialDelay.TotalSeconds * Math.Pow(Multiplier, attempt - 1)) * randDouble);

        /// <summary>Whether another attempt is allowed after <paramref name="attempt"/> failures in a row.</summary>
        internal bool CanRetry(int attempt) =>
        MaxAttempts is null || attempt < MaxAttempts;
    }
}
