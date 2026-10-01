// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
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
}
