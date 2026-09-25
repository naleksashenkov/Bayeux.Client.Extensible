// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>An API key in a custom header. The smallest possible provider.</summary>
public sealed class ApiKeyAuthProvider : IAuthProvider
{
    // Written by callers, read by the polling thread: volatile is what makes a replacement visible.
    private volatile string _apiKey;

    public ApiKeyAuthProvider(string apiKey) =>
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

    public void Update(string apiKey) =>
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A bearer token, implementing the typed variant so callers get an <c>UpdateCredentials</c>
/// and a notification when it is replaced.
/// </summary>
public sealed class BearerTokenAuthProvider : IAuthProvider<string>
{
    private volatile AuthenticationHeaderValue _header;

    public event EventHandler<OnCredentialsUpdatedEventArgs>? OnCredentialsUpdated;

    public BearerTokenAuthProvider(string token) =>
        _header = new AuthenticationHeaderValue("Bearer", token);

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = _header;
        return Task.CompletedTask;
    }

    public void UpdateCredentials(string token)
    {
        try
        {
            _header = new AuthenticationHeaderValue("Bearer", token);
            OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create());
        }
        catch (Exception ex)
        {
            OnCredentialsUpdated?.Invoke(this, OnCredentialsUpdatedEventArgs.Create(ex));
        }
    }
}

/// <summary>Using a custom provider, and refreshing a token before it expires.</summary>
public static class CustomAuthExample
{
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

        await using var poller = new CometDPoller(
            http,
            new CometdPollerOptions(channels, "cometd"),
            auth,
            onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await poller.ConnectAsync();

        // The poller does not refresh anything on its own. Rotate the token on your own schedule;
        // the next request picks it up with no reconnect and no gap in delivery.
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
}
