// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net;
using System.Text;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Bayeux over HTTP long-polling: every batch of messages is one POST, and its response carries
    /// the replies - and whatever events the server had queued for the session.
    /// </summary>
    /// <remarks>
    /// Everything HTTP lives here - the address, credentials, cookies, status codes - and nothing of
    /// the protocol: what the messages mean is the client's business.
    /// </remarks>
    internal sealed class LongPollingTransport : IBayeuxTransport
    {
        private readonly HttpClient _client;

        private readonly IAuthProvider _authProvider;

        private readonly string _path;

        // The client's, not this transport's: the session's identity, kept apart from every other
        // client that shares the same HttpClient.
        private readonly CookieContainer _cookieContainer;

        // Read once: the client is the caller's and may be shared, so its BaseAddress could change
        // under a running client.
        private readonly Uri _baseAddress;

        /// <inheritdoc/>
        public string ConnectionType => "long-polling";

        /// <inheritdoc/>
        /// <remarks><see cref="HttpClient.Timeout"/>, or <c>null</c> when it is infinite.</remarks>
        public TimeSpan? RequestTimeout => _client.Timeout == Timeout.InfiniteTimeSpan ? null : _client.Timeout;

        /// <summary>Creates the transport over the caller's client.</summary>
        /// <param name="httpClient">The client to send with; its handler must have <c>UseCookies = false</c>.</param>
        /// <param name="authProvider">Applied to every request.</param>
        /// <param name="path">The endpoint path, relative to the HttpClient's base address.</param>
        /// <param name="cookie">The client's cookie store.</param>
        /// <exception cref="ArgumentException">The client has no base address.</exception>
        public LongPollingTransport(
            HttpClient httpClient, 
            IAuthProvider authProvider, 
            string path, 
            CookieContainer cookie)
        {
            _client = httpClient;
            _baseAddress = httpClient.BaseAddress ?? throw new ArgumentException("Base address is not set", nameof(httpClient));
            _authProvider = authProvider;
            _path = path;
            _cookieContainer = cookie;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Meta messages go to <c>{path}/{type}</c>, as CometD clients send them; application
        /// messages to the path itself. Cookies are saved before the status is checked, because an
        /// error response may still rotate the session cookie. A non-success status throws
        /// <see cref="BayeuxHttpException"/>.
        /// </remarks>
        public async Task<BayeuxResponseMessageModel[]> SendAsync(BayeuxRequestModel[] messages, CancellationToken cancellationToken)
        {
            if (messages == null || messages.Length == 0)
                throw new ArgumentException("Messages are null or empty");

            var endpoint = messages.First().Endpoint;

            var uri = new Uri(_baseAddress, endpoint is null ? _path : $"{_path}/{endpoint}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                await _authProvider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
                
                ApplyCookies(request, uri);
                request.Content = new StringContent(BayeuxRequestModel.FormLongPollingRequestJson(messages), Encoding.UTF8, "application/json");

                using (var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
                {
                    SaveCookies(response, uri);

                    if (!response.IsSuccessStatusCode)
                        throw new BayeuxHttpException(response.StatusCode, response.ReasonPhrase);

                    var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (string.IsNullOrEmpty(responseText))
                        return [];

                    var responseMessages = JsonSerializer.Deserialize<BayeuxResponseMessageModel[]>(responseText) ?? [];

                    return responseMessages;
                }
            }
        }
        
        /// <summary>Nothing to release.</summary>
        /// <remarks>Owns nothing: the HttpClient is the caller's, the cookies the client's.</remarks>
        public ValueTask DisposeAsync() => default;

        /// <summary>Attaches this client's cookies to an outgoing request.</summary>
        /// <param name="request">The request to decorate.</param>
        /// <param name="uri">The target, used to select cookies by domain and path.</param>
        /// <remarks>
        /// Done per request rather than by the HTTP handler so that Bayeux clients sharing one HttpClient
        /// keep separate sessions. This is why the handler must have <c>UseCookies = false</c>.
        /// </remarks>
        private void ApplyCookies(HttpRequestMessage request, Uri uri)
        {
            var cookieHeader = _cookieContainer.GetCookieHeader(uri);

            if (!string.IsNullOrEmpty(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        /// <summary>Captures cookies the server set, including CometD's <c>BAYEUX_BROWSER</c>.</summary>
        /// <param name="response">The response to read.</param>
        /// <param name="uri">The request target the cookies belong to.</param>
        private void SaveCookies(HttpResponseMessage response, Uri uri)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
                return;
            
            foreach (var cookie in cookies)
            {
                _cookieContainer.SetCookies(uri, cookie);
            }
        }
    }
}