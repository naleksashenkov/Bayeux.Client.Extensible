// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;
using Xunit;

namespace Bayeux.Client.Extensible.IntegrationTests;

/// <summary>
/// Runs the poller against the CometD reference implementation, which the fake server in the unit
/// tests can only imitate. Skipped unless <c>BAYEUX_COMETD_URL</c> points at a running instance;
/// see <c>tests/cometd-server</c> for how to start one.
/// </summary>
public class RealCometDTests
{
    private static string? Url => Environment.GetEnvironmentVariable("BAYEUX_COMETD_URL");

    private static bool Available => !string.IsNullOrEmpty(Url);

    private static HttpClient CreateClient(TimeSpan? timeout = null) =>
        new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(Url!),
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };

    private static async Task ControlAsync(string action)
    {
        using var control = new HttpClient { BaseAddress = new Uri(Url!) };
        await control.GetStringAsync("control/" + action);
    }

    private static (CometdPollerOptions options, ConcurrentQueue<JsonElement> received) Options(string channel)
    {
        var received = new ConcurrentQueue<JsonElement>();
        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels[channel] = (e, _) => { received.Enqueue(e.Data); return Task.CompletedTask; };
        return (new CometdPollerOptions(channels, "cometd"), received);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    [SkippableFact]
    public async Task Full_session_against_the_reference_server()
    {
        Skip.IfNot(Available, "BAYEUX_COMETD_URL is not set.");

        using var http = CreateClient();
        var (options, received) = Options("/topic/real");

        OnPollerDisconnectedEventArgs? disconnected = null;
        var done = new TaskCompletionSource();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance,
            (_, e) => { disconnected = e; done.TrySetResult(); });

        await poller.ConnectAsync();
        Assert.False(string.IsNullOrEmpty(poller.ClientId));

        await Task.Delay(500);
        await ControlAsync("publish?channel=/topic/real&text=from-cometd");

        Assert.True(await WaitForAsync(() => received.Count >= 1));
        Assert.True(received.TryPeek(out var payload));
        Assert.Equal("from-cometd", payload.GetProperty("text").GetString());

        await poller.DisconnectAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(DisconnectReason.TokenCancellation, disconnected!.Reason);
        Assert.True(disconnected.IsSuccess);
    }

    [SkippableFact]
    public async Task Server_initiated_disconnect_stops_the_poller()
    {
        Skip.IfNot(Available, "BAYEUX_COMETD_URL is not set.");

        using var http = CreateClient();
        var (options, _) = Options("/topic/kill");

        OnPollerDisconnectedEventArgs? disconnected = null;
        var done = new TaskCompletionSource();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance,
            (_, e) => { disconnected = e; done.TrySetResult(); });

        await poller.ConnectAsync();
        await ControlAsync("kill?clientId=" + poller.ClientId);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // CometD's ServerSession.disconnect() sends /meta/disconnect to the client. The
        // specification requires the client to stop rather than handshake again, so this is a
        // deliberate shutdown and not a recoverable session loss.
        Assert.Equal(DisconnectReason.ServerRequirement, disconnected!.Reason);
        Assert.True(disconnected.IsSuccess);
    }

    [SkippableFact]
    public async Task Sharing_a_cookie_container_makes_CometD_demote_a_session()
    {
        Skip.IfNot(Available, "BAYEUX_COMETD_URL is not set.");

        using var http = CreateClient();
        var shared = new CookieContainer();

        var (first, _) = Options("/topic/share1");
        var (second, _) = Options("/topic/share2");
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var p1 = new CometDPoller(http, first, NoAuthProvider.Instance, (_, _) => { }, shared);
        await using var p2 = new CometDPoller(http, second, NoAuthProvider.Instance, (_, _) => { }, shared);

        p1.OnError += (_, e) => errors.Enqueue(e);
        p2.OnError += (_, e) => errors.Enqueue(e);

        await p1.ConnectAsync();
        await p2.ConnectAsync();

        Assert.True(await WaitForAsync(() => errors.Any(e => e.Source == ErrorSource.Connect)));
        Assert.All(errors.Where(e => e.Source == ErrorSource.Connect), e => Assert.False(e.IsFatal));
    }

    [SkippableFact]
    public async Task Private_cookie_containers_keep_sessions_independent()
    {
        Skip.IfNot(Available, "BAYEUX_COMETD_URL is not set.");

        using var http = CreateClient();

        var (first, r1) = Options("/topic/iso1");
        var (second, r2) = Options("/topic/iso2");
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var p1 = new CometDPoller(http, first, NoAuthProvider.Instance, (_, _) => { });
        await using var p2 = new CometDPoller(http, second, NoAuthProvider.Instance, (_, _) => { });

        p1.OnError += (_, e) => errors.Enqueue(e);
        p2.OnError += (_, e) => errors.Enqueue(e);

        await p1.ConnectAsync();
        await p2.ConnectAsync();

        Assert.NotEqual(p1.ClientId, p2.ClientId);

        await ControlAsync("publish?channel=/topic/iso1&text=one");
        await ControlAsync("publish?channel=/topic/iso2&text=two");

        Assert.True(await WaitForAsync(() => r1.Count >= 1 && r2.Count >= 1));
        Assert.DoesNotContain(errors, e => e.Source == ErrorSource.Connect);
    }

    [SkippableFact]
    public async Task Batched_subscribe_and_unsubscribe_are_accepted_by_the_reference_server()
    {
        Skip.IfNot(Available, "BAYEUX_COMETD_URL is not set.");

        using var http = CreateClient();
        var (options, _) = Options("/topic/batch-setup");

        var a = new ConcurrentQueue<JsonElement>();
        var b = new ConcurrentQueue<JsonElement>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // The whole point of the batch: several messages in one array, which a real server must
        // answer one reply per message. A stub can be written to agree with the client; this cannot.
        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/batch-a"] = (e, _) => { a.Enqueue(e.Data); return Task.CompletedTask; },
            ["/topic/batch-b"] = (e, _) => { b.Enqueue(e.Data); return Task.CompletedTask; }
        });

        await Task.Delay(500);
        await ControlAsync("publish?channel=/topic/batch-a&text=one");
        await ControlAsync("publish?channel=/topic/batch-b&text=two");

        Assert.True(await WaitForAsync(() => a.Count >= 1 && b.Count >= 1));

        await poller.UnsubscribeChannelsAsync(["/topic/batch-a", "/topic/batch-b"]);

        Assert.DoesNotContain("/topic/batch-a", options.Channels.Keys);
        Assert.DoesNotContain("/topic/batch-b", options.Channels.Keys);

        var seen = a.Count + b.Count;

        await Task.Delay(500);
        await ControlAsync("publish?channel=/topic/batch-a&text=three");
        await ControlAsync("publish?channel=/topic/batch-b&text=four");

        // The server must have dropped both subscriptions, not just the first message in the array.
        Assert.False(await WaitForAsync(() => a.Count + b.Count > seen, 2500));
    }
}
