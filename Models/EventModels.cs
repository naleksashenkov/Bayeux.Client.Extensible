// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Authentication.EventModels
{
    /// <summary>Reports the outcome of replacing an authentication provider's credentials.</summary>
    /// <remarks>
    /// This describes only whether the provider could encode the new credentials locally. Whether
    /// the server accepts them is not known until the next request is answered.
    /// </remarks>
    public class OnCredentialsUpdatedEventArgs : EventArgs
    {
        /// <summary>Whether the credentials were replaced.</summary>
        public bool IsSuccess { get; }

        /// <summary>Why the replacement failed, or <c>null</c> when it succeeded.</summary>
        public Exception? Error { get; }

        /// <summary>Creates the arguments.</summary>
        /// <param name="isSuccess">Whether the credentials were replaced.</param>
        /// <param name="error">The failure, if any.</param>
        public OnCredentialsUpdatedEventArgs(bool isSuccess, Exception? error = null)
        {
            IsSuccess = isSuccess;
            Error = error;
        }

        /// <summary>Creates the arguments, deriving success from the presence of an error.</summary>
        /// <param name="error">The failure, or <c>null</c> for success.</param>
        /// <returns>The event arguments.</returns>
        public static OnCredentialsUpdatedEventArgs Create(Exception? error = null) =>
            new OnCredentialsUpdatedEventArgs(
                error == null,
                error);
    }

    /// <summary>
    /// Reports that the polling loop has stopped. Raised exactly once per connection, and only
    /// after a connection was established &#8212; a failed <c>ConnectAsync</c> throws instead.
    /// </summary>
    public class OnPollerDisconnectedEventArgs : EventArgs
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
    public class OnPollerErrorEventArgs : EventArgs
    {
        /// <summary>The error.</summary>
        public Exception Error { get; }

        /// <summary>Which operation produced it.</summary>
        public ErrorSource Source { get; }

        /// <summary>
        /// <c>true</c> when this error ends the polling loop and a disconnect event will follow;
        /// <c>false</c> when the poller carries on.
        /// </summary>
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
