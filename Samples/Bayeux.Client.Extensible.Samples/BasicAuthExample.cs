// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// HTTP Basic authentication, with credentials replaced at runtime.
/// </summary>
public static class BasicAuthExample
{
    public static async Task RunAsync(Uri serverBaseAddress, string user, string password)
    {
        // UseCookies = false is required: the Bayeux client manages session cookies per request, so
        // that several Bayeux clients can share one HttpClient without their sessions colliding.
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/orders"] = (e, _) => { Console.WriteLine($"order: {e.Data}"); return Task.CompletedTask; };

        var options = new BayeuxClientOptions(channels, "cometd");

        // Keep the provider: it is how credentials are rotated later.
        var auth = new HttpBasicAuthProvider(new BasicAuthCredentials(user, password));

        auth.OnCredentialsUpdated += (_, e) =>
            Console.WriteLine(e.IsSuccess ? "credentials replaced" : $"replace failed: {e.Error?.Message}");

        await using var client = new BayeuxClient(
            http,
            options,
            auth,
            onDisconnected: (_, e) =>
                Console.WriteLine($"stopped: {e.Reason}, clean: {e.IsSuccess}, {e.Error?.Message}"));

        client.OnError += (_, e) =>
            Console.WriteLine($"[{e.Source}] {e.Error.Message} (fatal: {e.IsFatal})");

        await client.ConnectAsync();
        Console.WriteLine($"connected, session {client.ClientId}");

        // A password rotation takes effect on the next request. No reconnect, no lost messages,
        // and the Bayeux session id does not change.
        auth.UpdateCredentials(new BasicAuthCredentials(user, "the-new-password"));

        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}
