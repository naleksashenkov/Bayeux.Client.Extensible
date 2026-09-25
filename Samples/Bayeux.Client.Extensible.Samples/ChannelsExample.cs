// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// Wildcard subscriptions, subscribing at runtime, and unsubscribing.
/// </summary>
public static class ChannelsExample
{
    public static async Task RunAsync(Uri serverBaseAddress)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();

        // "/topic/*" matches one further segment: /topic/orders, /topic/alerts.
        // It does not match /topic/orders/created — that is one segment deeper. e.Channel says which
        // one this event came on; a pattern subscription has no other way to tell them apart.
        //
        // Handlers are asynchronous and awaited: the next event waits until this one is written.
        // The token is cancelled when the poller stops, so a write in progress does not hold up
        // DisconnectAsync.
        channels["/topic/*"] = async (e, cancellationToken) =>
            await File.AppendAllTextAsync("topic.log", $"{e.Channel}: {e.Data}{Environment.NewLine}", cancellationToken);

        // "/v2/me/**" matches any depth: /v2/me/calls, /v2/me/calls/17/ended. A handler with nothing
        // to await returns a completed task.
        channels["/v2/me/**"] = (e, _) =>
        {
            Console.WriteLine($"{e.Channel}: {e.Data}");
            return Task.CompletedTask;
        };

        await using var poller = new CometDPoller(
            http,
            new CometdPollerOptions(channels, "cometd"),
            NoAuthProvider.Instance,
            onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await poller.ConnectAsync();

        // Added while the poller runs. The whole set goes out as one Bayeux message, so this is a
        // single round trip. Both are re-subscribed automatically after later handshakes, so they
        // survive a dropped session.
        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/audit"] = (e, _) => { Console.WriteLine($"audit: {e.Data}"); return Task.CompletedTask; },
            ["/topic/billing"] = (e, _) => { Console.WriteLine($"billing: {e.Data}"); return Task.CompletedTask; }
        });

        await Task.Delay(TimeSpan.FromSeconds(30));

        // Unsubscribe by the same text used to subscribe. Removing "/topic/*" would not remove a
        // separately registered "/topic/audit", and vice versa.
        await poller.UnsubscribeChannelsAsync(["/topic/audit", "/topic/billing"]);

        await Task.Delay(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The two ways a batch can fail, and why they are worth telling apart.
    /// </summary>
    public static async Task RunPartialFailureAsync(CometDPoller poller)
    {
        try
        {
            await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/public"] = (e, _) => { Console.WriteLine($"public: {e.Data}"); return Task.CompletedTask; },
                ["/topic/restricted"] = (e, _) => { Console.WriteLine($"restricted: {e.Data}"); return Task.CompletedTask; }
            });
        }
        catch (BayeuxSubscriptionException ex)
        {
            // The server answered. Everything in Succeeded is subscribed and delivering; everything
            // in Failures was rejected and has been rolled back, so Options.Channels still matches
            // what the server believes. Retrying only the failed channels is safe.
            Console.WriteLine($"subscribed: {string.Join(", ", ex.Succeeded)}");

            foreach (var failure in ex.Failures)
                Console.WriteLine($"rejected {failure.Key}: {failure.Value.Message}");
        }
        catch (HttpRequestException ex)
        {
            // The request never got an answer, so nothing is known about what the server did. The
            // entire batch was rolled back — no channel in it is subscribed.
            Console.WriteLine($"nothing was subscribed: {ex.Message}");
        }
    }

    /// <summary>
    /// Overlapping patterns each receive the message &#8212; that is CometD's behaviour, not a
    /// quirk of this client. Worth knowing before registering a broad pattern next to a narrow one.
    /// </summary>
    public static async Task RunOverlappingAsync(Uri serverBaseAddress)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();

        // A message on /topic/orders reaches both handlers, once each.
        channels["/topic/**"] = (e, _) => { Console.WriteLine($"audit log, {e.Channel}: {e.Data}"); return Task.CompletedTask; };
        channels["/topic/orders"] = (e, _) => { Console.WriteLine($"order processing: {e.Data}"); return Task.CompletedTask; };

        await using var poller = new CometDPoller(
            http,
            new CometdPollerOptions(channels, "cometd"),
            NoAuthProvider.Instance,
            onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await poller.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}
