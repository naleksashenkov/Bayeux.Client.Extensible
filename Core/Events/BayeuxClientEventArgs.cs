// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Reports that the connect loop has stopped. Raised exactly once per connection, and only
    /// after a connection was established &#8212; a failed <c>ConnectAsync</c> throws instead.
    /// </summary>
    public sealed class BayeuxDisconnectedEventArgs : EventArgs
    {
        /// <summary>Why the loop stopped.</summary>
        public DisconnectReason Reason { get; }

        /// <summary>
        /// The error that stopped the loop, or <c>null</c> when it stopped for a non-error reason.
        /// </summary>
        public Exception? Error { get; }

        /// <summary>
        /// Why the <c>/meta/disconnect</c> exchange failed, or <c>null</c> when it succeeded or was
        /// not attempted. A failure here is informational: the session ends either way, and the
        /// server reclaims it when its own timeout elapses.
        /// </summary>
        public Exception? DisconnectError { get; }

        /// <summary>
        /// <c>true</c> when the loop stopped for a non-error reason <em>and</em> the disconnect
        /// completed without error. <c>false</c> if an error stopped the loop, or the disconnect
        /// itself failed &#8212; the corresponding property then carries the cause.
        /// </summary>
        public bool IsSuccess => Error == null && DisconnectError == null;

        /// <summary>Creates the arguments.</summary>
        /// <param name="reason">Why the loop stopped.</param>
        /// <param name="error">The error that stopped it, if any.</param>
        /// <param name="disconnectError">The disconnect failure, if any.</param>
        public BayeuxDisconnectedEventArgs(DisconnectReason reason, Exception? error = null, Exception? disconnectError = null)
        {
            Reason = reason;
            Error = error;
            DisconnectError = disconnectError;
        }

        /// <summary>Creates the arguments.</summary>
        /// <param name="reason">Why the loop stopped.</param>
        /// <param name="error">The error that stopped it, if any.</param>
        /// <param name="disconnectError">The disconnect failure, if any.</param>
        /// <returns>The event arguments.</returns>
        public static BayeuxDisconnectedEventArgs Create(DisconnectReason reason, Exception? error = null, Exception? disconnectError = null) =>
            new BayeuxDisconnectedEventArgs(reason, error, disconnectError);
    }

    /// <summary>
    /// Reports one error observed by the client. May be raised any number of times;
    /// <see cref="IsFatal"/> says whether the client is about to stop.
    /// </summary>
    public sealed class BayeuxErrorEventArgs : EventArgs
    {
        /// <summary>The error.</summary>
        public Exception Error { get; }

        /// <summary>Which operation produced it.</summary>
        public ErrorSource Source { get; }

        /// <summary>
        /// <c>true</c> when this error stops the client: <c>ConnectAsync</c> throws it, or the
        /// connect loop ends and a disconnect event follows. <c>false</c> when the client carries on.
        /// </summary>
        /// <remarks>
        /// With <see cref="BayeuxClientOptions.ReconnectOptions"/> set, a failed connect or handshake
        /// inside the loop is not fatal: the client may try again. Whether it did or gave up in the
        /// end is reported by the disconnect event, never here.
        /// </remarks>
        public bool IsFatal { get; }

        /// <summary>Creates the arguments.</summary>
        /// <param name="error">The error.</param>
        /// <param name="source">Which operation produced it.</param>
        /// <param name="isFatal">Whether the client stops because of it.</param>
        public BayeuxErrorEventArgs(Exception error, ErrorSource source, bool isFatal)
        {
            Error = error;
            Source = source;
            IsFatal = isFatal;
        }
    }

    /// <summary>Reports a change of the client's <see cref="BayeuxClientState"/>.</summary>
    public sealed class BayeuxStateChangedEventArgs : EventArgs
    {
        /// <summary>The state the client was in <em>before</em> this change.</summary>
        public BayeuxClientState PreviousState { get; }

        /// <summary>The state the client is in now.</summary>
        public BayeuxClientState CurrentState { get; }

        /// <summary>
        /// The error that caused the change - for <see cref="BayeuxClientState.Reconnecting"/> after a
        /// failure, and <see cref="BayeuxClientState.Disconnected"/> after one that was not retried or a
        /// failed <c>ConnectAsync</c> - or <c>null</c> when none did.
        /// </summary>
        public Exception? Error { get; }

        /// <summary>Creates the arguments.</summary>
        /// <param name="previousState">The state before the change.</param>
        /// <param name="currentState">The state after it.</param>
        /// <param name="error">The error that caused it, if any.</param>
        public BayeuxStateChangedEventArgs(BayeuxClientState previousState, BayeuxClientState currentState, Exception? error = null)
        {
            PreviousState = previousState;
            CurrentState = currentState;
            Error = error;
        }
    }
}