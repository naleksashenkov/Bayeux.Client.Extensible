// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Reports that the polling loop has stopped. Raised exactly once per connection, and only
    /// after a connection was established &#8212; a failed <c>ConnectAsync</c> throws instead.
    /// </summary>
    public sealed class OnPollerDisconnectedEventArgs : EventArgs
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
        public OnPollerDisconnectedEventArgs(DisconnectReason reason, Exception? error = null, Exception? disconnectError = null)
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
        public static OnPollerDisconnectedEventArgs Create(DisconnectReason reason, Exception? error = null, Exception? disconnectError = null) =>
            new OnPollerDisconnectedEventArgs(reason, error, disconnectError);
    }

    /// <summary>
    /// Reports one error observed by the poller. May be raised any number of times;
    /// <see cref="IsFatal"/> says whether the poller is about to stop.
    /// </summary>
    public sealed class OnPollerErrorEventArgs : EventArgs
    {
        /// <summary>The error.</summary>
        public Exception Error { get; }

        /// <summary>Which operation produced it.</summary>
        public ErrorSource Source { get; }

        /// <summary>
        /// <c>true</c> when this error stops the poller: <c>ConnectAsync</c> throws it, or the
        /// polling loop ends and a disconnect event follows. <c>false</c> when the poller carries on.
        /// </summary>
        /// <remarks>
        /// With <see cref="PollerOptions.ReconnectOptions"/> set, a failed connect or handshake
        /// inside the loop is not fatal: the poller may try again. Whether it did or gave up in the
        /// end is reported by the disconnect event, never here.
        /// </remarks>
        public bool IsFatal { get; }

        /// <summary>Creates the arguments.</summary>
        /// <param name="error">The error.</param>
        /// <param name="source">Which operation produced it.</param>
        /// <param name="isFatal">Whether the poller stops because of it.</param>
        public OnPollerErrorEventArgs(Exception error, ErrorSource source, bool isFatal)
        {
            Error = error;
            Source = source;
            IsFatal = isFatal;
        }
    }
}