// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// A provider of your own: a gateway that wants every request signed with a shared secret -
/// a timestamp and an HMAC of it - rather than a key sent as it is. None of the built-in
/// providers does that; one method does.
/// </summary>
/// <remarks>
/// The signature changes with every request, which is fine: <c>ApplyAsync</c> runs before each
/// one. It only reads state and computes, as it should - no network I/O on the client's hot
/// path. Over a WebSocket it runs once per socket, for the upgrade, so a gateway that checks the
/// timestamp's age only lets the socket open; it cannot re-check frames.
/// </remarks>
public sealed class SignedRequestAuthProvider : IAuthProvider
{
    private readonly string _keyId;
    private readonly byte[] _secret;

    public SignedRequestAuthProvider(string keyId, byte[] secret)
    {
        _keyId = keyId ?? throw new ArgumentNullException(nameof(keyId));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var hmac = new HMACSHA256(_secret);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(_keyId + ":" + timestamp)));

        request.Headers.TryAddWithoutValidation("X-Key-Id", _keyId);
        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Authentication beyond Basic: a bearer token refreshed before it expires, an API key, and a
/// provider of your own.
/// </summary>
public static class CustomAuthExample
{
    /// <summary>A bearer token the application refreshes on its own schedule.</summary>
    public static async Task RunAsync(Uri serverBaseAddress, Func<CancellationToken, Task<string>> getToken)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/events"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; };

        var auth = new BearerTokenAuthProvider(await getToken(CancellationToken.None));

        await using var client = new BayeuxClient(
            http,
            new BayeuxClientOptions(channels, "cometd"),
            auth,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await client.ConnectAsync();

        // The provider does not refresh anything on its own. Replace the token before it expires;
        // the next request picks it up with no reconnect and no gap in delivery. Over a WebSocket,
        // the next socket does: the current one was authenticated when it opened.
        using var refreshing = new CancellationTokenSource();

        var refresher = Task.Run(async () =>
        {
            while (!refreshing.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(30), refreshing.Token);
                auth.UpdateCredentials(await getToken(refreshing.Token));
            }
        });

        await Task.Delay(TimeSpan.FromMinutes(1));

        refreshing.Cancel();
        try { await refresher; } catch (OperationCanceledException) { }
    }

    /// <summary>An API key in a header of its own; the gateway decides the header's name.</summary>
    public static async Task RunApiKeyAsync(Uri serverBaseAddress, string apiKey)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        // A name HttpClient would not send - "X API Key", "Content-Type" - is refused right here,
        // rather than dropped from every request without a word.
        var auth = new ApiKeyAuthProvider(new ApiKeyCredentials(apiKey, "Ocp-Apim-Subscription-Key"));

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/events"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        await using var client = new BayeuxClient(
            http,
            new BayeuxClientOptions(channels, "cometd"),
            auth,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await client.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    /// <summary>The signing provider above, used like any other.</summary>
    public static async Task RunSignedAsync(Uri serverBaseAddress, string keyId, byte[] secret)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/events"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        await using var client = new BayeuxClient(
            http,
            new BayeuxClientOptions(channels, "cometd"),
            new SignedRequestAuthProvider(keyId, secret),
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await client.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// A token the provider renews itself, and drops when the server revokes it early. The token
    /// source is yours: MSAL, an OAuth client, a call to your own endpoint.
    /// </summary>
    public static async Task RunRefreshingAsync(Uri serverBaseAddress, Func<CancellationToken, Task<AccessToken>> tokenSource)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        // Fetched on the first request, renewed a minute before it expires. Called once however
        // many requests find it due at the same moment.
        var auth = new RefreshingTokenAuthProvider(tokenSource);
        auth.OnCredentialsUpdated += (_, e) =>
        {
            if (!e.IsSuccess)
                Console.WriteLine($"token renewal failed: {e.Error?.Message}");
        };

        // Salesforce's answer to a revoked token. Retried, since the next attempt has a new one.
        static bool Revoked(Exception e) => e is BayeuxConnectException { Error: "401::Authentication invalid" };

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/events"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        var options = new BayeuxClientOptions(
            channels,
            "cometd",
            reconnectOptions: new ReconnectOptions(shouldRetry: e => Revoked(e) || ReconnectOptions.IsRetriable(e)));

        await using var client = new BayeuxClient(
            http,
            options,
            auth,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        client.OnError += (_, e) =>
        {
            if (Revoked(e.Error))
                auth.Invalidate();
        };

        await client.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// A provider in one lambda: a gateway that wants its subscription key and the user's token on
    /// every request - two providers, applied in turn.
    /// </summary>
    public static async Task RunDelegateAsync(Uri serverBaseAddress, string gatewayKey, string userToken, string tenant)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var key = new ApiKeyAuthProvider(new ApiKeyCredentials(gatewayKey, "Ocp-Apim-Subscription-Key"));
        var token = new BearerTokenAuthProvider(userToken);

        // Each keeps its own UpdateCredentials; the lambda only puts them together, plus a header
        // none of them sets.
        var auth = new DelegateAuthProvider(async (request, ct) =>
        {
            await key.ApplyAsync(request, ct);
            await token.ApplyAsync(request, ct);
            request.Headers.TryAddWithoutValidation("X-Tenant", tenant);
        });

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/events"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        await using var client = new BayeuxClient(
            http,
            new BayeuxClientOptions(channels, "cometd"),
            auth,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await client.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}
