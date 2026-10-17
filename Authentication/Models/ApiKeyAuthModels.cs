// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Authentication
{
    /// <summary>An API key and the request header it travels in.</summary>
    /// <remarks>
    /// Immutable and checked once, here: a key or header name that could not be sent is refused
    /// when it is created - or, through <see cref="ApiKeyAuthProvider.UpdateCredentials"/>, when it
    /// would replace a working one - rather than failing every request later.
    /// </remarks>
    public sealed class ApiKeyCredentials
    {
        /// <summary>The key, sent as it is.</summary>
        public string ApiKey { get; }

        /// <summary>The header carrying it: <c>X-API-Key</c> unless the server expects another.</summary>
        public string HeaderName { get; }

        /// <summary>Creates the credentials.</summary>
        /// <param name="apiKey">The key.</param>
        /// <param name="headerName">
        /// The header to send it in, such as <c>api-key</c> or <c>Ocp-Apim-Subscription-Key</c>;
        /// <c>X-API-Key</c> by default.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The key is empty or contains a line break; or the header name is empty, or not one a
        /// request may carry - it contains a space, or names content such as <c>Content-Type</c>.
        /// </exception>
        public ApiKeyCredentials(string apiKey, string headerName = "X-API-Key")
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("The API key must not be empty.", nameof(apiKey));

            if (apiKey.IndexOfAny([ '\r', '\n' ]) != -1)
                throw new ArgumentException("The API key must not contain '\\r' or '\\n' symbols.", nameof(apiKey));

            if (string.IsNullOrWhiteSpace(headerName))
                throw new ArgumentException("The header must not be empty.", nameof(headerName));

            // The only reliable way to learn whether HttpClient accepts the name: ask it. Refused, it
            // would be dropped without a word, and the key never sent.
            using (var blank = new HttpRequestMessage())
                if (!blank.Headers.TryAddWithoutValidation(headerName, "blank"))
                    throw new ArgumentException(
                        $"Header \"{headerName}\" is not allowed to send.",
                        nameof(headerName));

            ApiKey = apiKey;
            HeaderName = headerName;
        }
    }
}