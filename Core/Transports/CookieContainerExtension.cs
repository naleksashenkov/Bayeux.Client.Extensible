// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net;

namespace Bayeux.Client.Extensible.Core
{
    internal static class CookieContainerExtension
    {
        /// <summary>Stores the cookies of a response, dropping those the address may not hold.</summary>
        /// <param name="container">The client's cookie store.</param>
        /// <param name="uri">The address the response came from.</param>
        /// <param name="cookies">The values of its <c>Set-Cookie</c> headers.</param>
        /// <remarks>
        /// A cookie for another domain is the server's mistake, not a reason to fail the request that
        /// brought it. An HTTP handler drops such a cookie in the same way.
        /// </remarks>
        public static void SaveCookies(this CookieContainer container, Uri uri, IEnumerable<string> cookies)
        {
            foreach (var cookie in cookies)
            {
                try
                {
                    container.SetCookies(uri, cookie);
                }
                catch (CookieException)
                {
                }
            }
        }
    }
}