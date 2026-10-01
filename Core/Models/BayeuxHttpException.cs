// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>The server answered a Bayeux request with a status other than success.</summary>
    /// <remarks>
    /// Derives from <see cref="HttpRequestException"/>, so code that already catches that keeps
    /// working. It exists because the base class carries a status code only on .NET 5 and later;
    /// this one carries it on every target.
    /// </remarks>
    public sealed class BayeuxHttpException : HttpRequestException
    {
        /// <summary>The status the server returned.</summary>
        #if NET5_0_OR_GREATER
            // Hides the base property, which is nullable because the base class also covers
            // failures with no response at all. Here there always is one.
            public new HttpStatusCode StatusCode { get; }
        #else
            public HttpStatusCode StatusCode { get; }
        #endif

        /// <summary>Creates the exception from a response.</summary>
        /// <param name="statusCode">The status the server returned.</param>
        /// <param name="reason">The reason phrase, if the server sent one.</param>
        public BayeuxHttpException(HttpStatusCode statusCode, string? reason)
        // On .NET 5 and later the base gets the status too, for code that reads it from there.
        #if NET5_0_OR_GREATER
            :base(FormatMessage(statusCode, reason), null, statusCode)
        #else
            :base(FormatMessage(statusCode, reason))
        #endif
        {
            StatusCode = statusCode;
        }

        private static string FormatMessage(HttpStatusCode statusCode, string? reason) =>
            $"Response status code does not indicate success: {(int)statusCode} ({reason ?? statusCode.ToString()}).";
    }
}
