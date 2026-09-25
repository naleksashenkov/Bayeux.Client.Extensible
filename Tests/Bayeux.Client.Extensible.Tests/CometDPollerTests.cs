// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Authentication.Models;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;
using Bayeux.Client.Extensible.Interfaces;
using Bayeux.Client.Extensible.Samples;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

public class CometDPollerTests
{
    private static (CometdPollerOptions options, ConcurrentQueue<JsonElement> received) Options(string channel)
    {
        var received = new ConcurrentQueue<JsonElement>();
        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels[channel] = On(data => received.Enqueue(data));
        return (new CometdPollerOptions(channels, "cometd"), received);
    }

    /// <summary>
    /// Waits for a task, failing the test rather than hanging the run if it never completes.
    /// Written out because Task.WaitAsync is .NET 6+ and this suite also runs on net472.
    /// </summary>
    /// <summary>As <see cref="WithTimeoutAsync(Task, TimeSpan)"/>, returning the task's result.</summary>
    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        await WithTimeoutAsync((Task)task, timeout).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();

        if (await Task.WhenAny(task, Task.Delay(timeout, cts.Token)).ConfigureAwait(false) != task)
            throw new TimeoutException($"The task did not complete within {timeout}.");

        cts.Cancel();          // stop the timer task
        await task.ConfigureAwait(false);   // surface the task's own exception, if any
    }

    /// <summary>A handler that does nothing, for channels a test only needs to be subscribed to.</summary>
    private static readonly BayeuxEventHandler Ignore = (_, _) => Task.CompletedTask;

    /// <summary>Adapts a synchronous callback to the handler signature, for tests that record data.</summary>
    private static BayeuxEventHandler On(Action<JsonElement> record) =>
        (e, _) =>
        {
            record(e.Data);
            return Task.CompletedTask;
        };

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task Handshake_request_matches_the_bayeux_wire_format()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/orders");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        var handshake = server.BodyFor("handshake");

        Assert.Contains("\"channel\":\"/meta/handshake\"", handshake);
        Assert.Contains("\"version\":\"1.0\"", handshake);
        Assert.Contains("\"supportedConnectionTypes\":[\"long-polling\"]", handshake);
        // The specification's handshake carries no clientId; the property must be omitted, not null.
        Assert.DoesNotContain("\"clientId\"", handshake);
        // Nor any ext when no extension is registered: a poller without extensions must send exactly
        // what it sent before the field existed. Fails if Ext is ever defaulted to an empty object.
        Assert.DoesNotContain("\"ext\"", handshake);
    }

    [Fact]
    public async Task Connect_and_subscribe_requests_carry_the_session_id()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/orders");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();
        await WaitForAsync(() => server.RequestBodiesSnapshot.Any(b => b.StartsWith("/cometd/connect")));

        var subscribe = server.BodyFor("subscribe");
        Assert.Contains("\"subscription\":\"/topic/orders\"", subscribe);
        Assert.Contains("\"clientId\":\"", subscribe);

        var connect = server.BodyFor("connect");
        Assert.Contains("\"connectionType\":\"long-polling\"", connect);
        Assert.Contains("\"clientId\":\"", connect);
    }

    [Fact]
    public async Task Broadcast_messages_reach_the_registered_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/orders");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.Publish("/topic/orders", new { status = "shipped" });

        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.True(received.TryPeek(out var payload));
        Assert.Equal("shipped", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Basic_credentials_are_sent_on_every_request()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/orders");
        var auth = new HttpBasicAuthProvider(new BasicAuthCredentials("svc", "p@ss"));

        await using var poller = new CometDPoller(http, options, auth, (_, _) => { });
        await poller.ConnectAsync();

        var headers = server.AuthHeadersSnapshot;

        Assert.NotEmpty(headers);
        Assert.All(headers, h => Assert.StartsWith("Basic ", h));
    }

    [Fact]
    public async Task Session_cookie_is_captured_and_returned()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/orders");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // The handshake has no cookie yet; everything after it must carry the one the server set.
        var afterHandshake = server.CookieHeadersSnapshot.Skip(1).ToList();

        Assert.NotEmpty(afterHandshake);
        Assert.All(afterHandshake, c => Assert.Contains("BAYEUX_BROWSER=", c));
    }

    [Fact]
    public async Task Unknown_client_triggers_a_rehandshake_and_resubscribe()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/alerts");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        var firstClientId = poller.ClientId;
        server.FailNextConnectsWith402 = 1;

        Assert.True(await WaitForAsync(() => server.HandshakeCount == 2));
        Assert.NotEqual(firstClientId, poller.ClientId);
        Assert.Equal(2, server.SubscriptionsSnapshot.Count(c => c == "/topic/alerts"));

        server.Publish("/topic/alerts", new { level = "warn" });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task Reconnect_none_stops_the_poller_without_sending_a_disconnect()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/x");

        OnPollerDisconnectedEventArgs? disconnected = null;
        var done = new TaskCompletionSource<bool>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance,
            (_, e) => { disconnected = e; done.TrySetResult(true); });

        await poller.ConnectAsync();
        server.StopOnNextConnect = true;

        await WithTimeoutAsync(done.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.ServerRequirement, disconnected!.Reason);
        Assert.True(disconnected.IsSuccess);
        Assert.Equal(0, server.DisconnectCount);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task Disconnect_reports_a_clean_shutdown()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/x");

        OnPollerDisconnectedEventArgs? disconnected = null;
        var done = new TaskCompletionSource<bool>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance,
            (_, e) => { disconnected = e; done.TrySetResult(true); });

        await poller.ConnectAsync();
        await poller.DisconnectAsync();
        await WithTimeoutAsync(done.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.TokenCancellation, disconnected!.Reason);
        Assert.True(disconnected.IsSuccess);
        Assert.Null(disconnected.Error);
        Assert.Null(disconnected.DisconnectError);
        Assert.Equal(1, server.DisconnectCount);
    }

    [Fact]
    public async Task Multiple_clients_advice_is_reported_once_and_is_not_fatal()
    {
        using var server = new FakeBayeuxServer { SendMultipleClients = true };
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/y");

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);

        await poller.ConnectAsync();
        await Task.Delay(2500);   // several poll cycles

        var demotions = errors.Where(e => e.Source == ErrorSource.Connect).ToList();

        Assert.Single(demotions);
        Assert.All(demotions, e => Assert.False(e.IsFatal));
    }

    [Fact]
    public async Task Connect_failure_throws_and_raises_no_disconnect_event()
    {
        // Nothing is listening on this port.
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:1/"),
            Timeout = Timeout.InfiniteTimeSpan
        };

        var (options, _) = Options("/topic/z");
        var raised = false;
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        poller.OnError += (_, e) => errors.Enqueue(e);

        await Assert.ThrowsAnyAsync<Exception>(() => poller.ConnectAsync());

        Assert.False(raised);
        Assert.Contains(errors, e => e.Source == ErrorSource.Http && e.IsFatal);
    }

    [Fact]
    public async Task Rejected_subscription_fails_the_connect()
    {
        using var server = new FakeBayeuxServer { RejectSubscribe = true };
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/denied");

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);

        // Setup uses the same exception as a runtime batch, so there is one type to catch either way.
        await Assert.ThrowsAsync<BayeuxSubscriptionException>(() => poller.ConnectAsync());
        Assert.Contains(errors, e => e.Source == ErrorSource.Subscribe);
    }

    [Fact]
    public async Task Poller_can_reconnect_after_a_disconnect()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/again");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });

        await poller.ConnectAsync();
        await poller.DisconnectAsync();
        await poller.ConnectAsync();

        Assert.Equal(2, server.HandshakeCount);

        server.Publish("/topic/again", new { ok = true });

        var delivered = await WaitForAsync(() => received.Count == 1);

        if (!delivered)
        {
            var diag = string.Join("\n", server.RequestBodiesSnapshot);
            Assert.Fail($"not delivered. clientId={poller.ClientId} handshakes={server.HandshakeCount} " +
                        $"subs=[{string.Join(",", server.SubscriptionsSnapshot)}]\nrequests:\n{diag}");
        }
    }

    [Fact]
    public async Task Client_timeout_below_the_server_hold_time_is_rejected()
    {
        using var server = new FakeBayeuxServer { AdviceTimeoutMs = 30000 };
        using var http = server.CreateClient(TimeSpan.FromSeconds(20));   // needs 30 + 5 margin
        var (options, _) = Options("/topic/timeout");

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.ConnectAsync());
        Assert.Contains(errors, e => e.Source == ErrorSource.Configuration);
    }

    [Fact]
    public async Task Subscribing_at_runtime_adds_a_channel_to_the_live_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        var extra = new ConcurrentQueue<JsonElement>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/second"] = On(d => extra.Enqueue(d))
        });

        Assert.Contains("/topic/second", server.SubscriptionsSnapshot);

        server.Publish("/topic/second", new { hello = true });
        Assert.True(await WaitForAsync(() => extra.Count == 1));
    }

    [Fact]
    public async Task Handler_exceptions_are_reported_but_do_not_stop_the_poller()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/boom"] = On(_ => throw new InvalidOperationException("handler bug"));
        var options = new CometdPollerOptions(channels, "cometd");

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();
        var disconnected = false;

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => disconnected = true);
        poller.OnError += (_, e) => errors.Enqueue(e);

        await poller.ConnectAsync();
        server.Publish("/topic/boom", new { x = 1 });

        Assert.True(await WaitForAsync(() => errors.Any(e => e.Source == ErrorSource.Handler)));
        Assert.All(errors.Where(e => e.Source == ErrorSource.Handler), e => Assert.False(e.IsFatal));
        Assert.False(disconnected);
    }


    [Fact]
    public async Task Basic_credentials_can_be_replaced_without_reconnecting()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/x");

        var auth = new HttpBasicAuthProvider(new BasicAuthCredentials("first", "one"));

        await using var poller = new CometDPoller(http, options, auth, (_, _) => { });
        await poller.ConnectAsync();

        var clientIdBefore = poller.ClientId;

        var expected = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes("second:two"));

        auth.UpdateCredentials(new BasicAuthCredentials("second", "two"));

        // The next poll cycle must carry the new credentials, with no new handshake.
        Assert.True(await WaitForAsync(() => server.AuthHeadersSnapshot.Any(h => h == expected)));
        Assert.Equal(clientIdBefore, poller.ClientId);   // no rehandshake
        Assert.Equal(1, server.HandshakeCount);
    }

    [Theory]
    // one further segment
    [InlineData("/topic/*", "/topic/orders", true)]
    [InlineData("/topic/*", "/topic/orders/created", false)]
    [InlineData("/v2/me/*", "/v2/me/calls", true)]          // wildcard deeper than two segments
    // any depth below
    [InlineData("/topic/**", "/topic/orders", true)]
    [InlineData("/topic/**", "/topic/orders/17/shipped", true)]
    [InlineData("/topic/**", "/other/x", false)]
    [InlineData("/topic/**", "/topicfoo/x", false)]         // prefix must end at a segment boundary
    public async Task Wildcard_subscriptions_receive_only_matching_channels(
        string pattern, string channel, bool shouldDeliver)
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options(pattern);

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.Publish(channel, new { ok = true });

        // The server delivers whatever is queued; matching is the client's job, so a negative
        // case means the message arrived and was correctly filtered out.
        var delivered = await WaitForAsync(() => received.Count == 1, shouldDeliver ? 5000 : 1500);

        Assert.Equal(shouldDeliver, delivered);
    }

    [Fact]
    public async Task Overlapping_patterns_each_receive_the_message()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var broad = new ConcurrentQueue<JsonElement>();
        var exact = new ConcurrentQueue<JsonElement>();

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/**"] = On(data => broad.Enqueue(data));
        channels["/topic/orders"] = On(data => exact.Enqueue(data));

        await using var poller = new CometDPoller(
            http, new CometdPollerOptions(channels, "cometd"), NoAuthProvider.Instance, (_, _) => { });

        await poller.ConnectAsync();
        server.Publish("/topic/orders", new { id = 1 });

        // CometD delivers to every matching subscription, so both handlers fire once.
        Assert.True(await WaitForAsync(() => broad.Count == 1 && exact.Count == 1));
    }

    [Fact]
    public async Task Unsubscribing_removes_the_channel_from_later_handshakes()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/gone");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.UnsubscribeChannelsAsync(["/topic/gone"]);

        Assert.Contains("/topic/gone", server.UnsubscriptionsSnapshot);
        Assert.DoesNotContain("/topic/gone", options.Channels.Keys);

        // The real point: the set replayed after a rehandshake no longer contains it.
        server.FailNextConnectsWith402 = 1;
        Assert.True(await WaitForAsync(() => server.HandshakeCount == 2));

        Assert.Equal(1, server.SubscriptionsSnapshot.Count(c => c == "/topic/gone"));

        server.Publish("/topic/gone", new { ignored = true });
        Assert.False(await WaitForAsync(() => received.Count > 0, 1500));
    }

    [Fact]
    public async Task Unsubscribing_channels_that_were_never_registered_reaches_no_server()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/present");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // Nothing to unsubscribe means nothing to ask the server, so the caller is told rather than
        // left believing a channel was dropped.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => poller.UnsubscribeChannelsAsync(["/topic/never-registered"]));

        Assert.Empty(server.UnsubscriptionsSnapshot);
        Assert.Contains("/topic/present", options.Channels.Keys);
    }

    [Fact]
    public void Channels_are_copied_so_the_callers_dictionary_cannot_add_unsubscribed_entries()
    {
        var source = new ConcurrentDictionary<string, BayeuxEventHandler>();
        source["/topic/one"] = Ignore;

        var options = new CometdPollerOptions(source, "cometd");

        // The caller still holds their own dictionary; mutating it must not reach the poller,
        // because an entry added that way would never be subscribed on the server.
        source["/topic/sneaked-in"] = Ignore;

        Assert.Contains("/topic/one", options.Channels.Keys);
        Assert.DoesNotContain("/topic/sneaked-in", options.Channels.Keys);
    }

    [Fact]
    public async Task Subscribing_at_runtime_is_visible_through_the_read_only_view()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/second"] = Ignore
        });

        Assert.Contains("/topic/second", options.Channels.Keys);

        await poller.UnsubscribeChannelsAsync(["/topic/second"]);

        Assert.DoesNotContain("/topic/second", options.Channels.Keys);
    }

    [Fact]
    public async Task A_batch_of_channels_is_sent_as_one_request()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/second"] = Ignore,
            ["/topic/third"] = Ignore
        });

        // The point of batching: one round trip, not one per channel.
        var subscribeBodies = server.RequestBodiesSnapshot.Where(b => b.StartsWith("/cometd/subscribe")).ToArray();

        Assert.Equal(2, subscribeBodies.Length);          // the setup batch, then this one
        Assert.Contains("/topic/second", subscribeBodies[1]);
        Assert.Contains("/topic/third", subscribeBodies[1]);

        Assert.Contains("/topic/second", server.SubscriptionsSnapshot);
        Assert.Contains("/topic/third", server.SubscriptionsSnapshot);
    }

    [Fact]
    public async Task Channels_already_registered_are_skipped_rather_than_re_sent()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/first"] = Ignore,
            ["/topic/second"] = Ignore
        });

        Assert.Equal(1, server.SubscriptionsSnapshot.Count(c => c == "/topic/first"));
        Assert.Contains("/topic/second", server.SubscriptionsSnapshot);
    }

    [Fact]
    public async Task Subscribing_a_batch_the_server_rejects_entirely_leaves_nothing_registered()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.RejectSubscribeFor("/topic/denied");

        await Assert.ThrowsAsync<BayeuxSubscriptionException>(
            () => poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/denied"] = Ignore
            }));

        Assert.DoesNotContain("/topic/denied", options.Channels.Keys);
    }

    [Fact]
    public async Task A_partly_rejected_subscribe_keeps_the_accepted_channels_and_rolls_back_the_rest()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        var accepted = new ConcurrentQueue<JsonElement>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.RejectSubscribeFor("/topic/denied");

        var error = await Assert.ThrowsAsync<BayeuxSubscriptionException>(
            () => poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/allowed"] = On(d => accepted.Enqueue(d)),
                ["/topic/denied"] = Ignore
            }));

        Assert.Equal(["/topic/allowed"], error.Succeeded);
        Assert.Contains("/topic/denied", error.Failures.Keys);

        // The local view must match what the server actually did, in both directions.
        Assert.Contains("/topic/allowed", options.Channels.Keys);
        Assert.DoesNotContain("/topic/denied", options.Channels.Keys);

        // And the accepted channel really is live.
        server.Publish("/topic/allowed", new { ok = true });
        Assert.True(await WaitForAsync(() => accepted.Count == 1));
    }

    [Fact]
    public async Task A_subscribe_that_never_reaches_the_server_rolls_the_whole_batch_back()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.NextSubscribeHttpStatus = 500;

        // Not a BayeuxSubscriptionException: nothing is known about what the server did, and the
        // absence of that type is what tells the caller the batch was not partly applied.
        await Assert.ThrowsAnyAsync<Exception>(
            () => poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/a"] = Ignore,
                ["/topic/b"] = Ignore
            }));

        Assert.DoesNotContain("/topic/a", options.Channels.Keys);
        Assert.DoesNotContain("/topic/b", options.Channels.Keys);
    }

    [Fact]
    public async Task A_refused_unsubscribe_restores_the_handler_because_messages_keep_arriving()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var kept = new ConcurrentQueue<JsonElement>();

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/stuck"] = On(d => kept.Enqueue(d));
        channels["/topic/free"] = Ignore;
        var options = new CometdPollerOptions(channels, "cometd");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.RejectUnsubscribeFor("/topic/stuck");

        var error = await Assert.ThrowsAsync<BayeuxSubscriptionException>(
            () => poller.UnsubscribeChannelsAsync(["/topic/stuck", "/topic/free"]));

        Assert.Equal(["/topic/free"], error.Succeeded);
        Assert.Contains("/topic/stuck", error.Failures.Keys);

        Assert.Contains("/topic/stuck", options.Channels.Keys);
        Assert.DoesNotContain("/topic/free", options.Channels.Keys);

        // The restored handler must still run: the server never dropped that subscription.
        server.Publish("/topic/stuck", new { still = "coming" });
        Assert.True(await WaitForAsync(() => kept.Count == 1));
    }

    [Fact]
    public async Task An_unsubscribe_that_never_reaches_the_server_keeps_every_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/a"] = Ignore;
        channels["/topic/b"] = Ignore;
        var options = new CometdPollerOptions(channels, "cometd");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.NextUnsubscribeHttpStatus = 500;

        await Assert.ThrowsAnyAsync<Exception>(
            () => poller.UnsubscribeChannelsAsync(["/topic/a", "/topic/b"]));

        // Both are still subscribed on the server, so both handlers must survive the failure.
        Assert.Contains("/topic/a", options.Channels.Keys);
        Assert.Contains("/topic/b", options.Channels.Keys);
    }

    [Fact]
    public async Task Events_carried_by_a_subscribe_reply_reach_their_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/first");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // CometD flushes the session queue onto whatever request arrives first, so an event can ride
        // in front of a /meta/subscribe reply. Verified against the reference server: only
        // /meta/handshake refuses to carry the queue.
        server.PiggybackOnNextSubscribe = ("/topic/first", new { text = "queued-event" });

        await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/second"] = Ignore
        });

        // Delivered by the time the call returns, not merely eventually: dropping it here would be
        // silent message loss with nothing in the log to find it by.
        Assert.Single(received);
        Assert.True(received.TryPeek(out var payload));
        Assert.Equal("queued-event", payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_handler_may_await_a_subscribe_it_starts_itself()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        CometDPoller? poller = null;
        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();

        // A handler awaiting a subscribe it starts itself, while its event rode on a subscribe reply
        // and the channel lock was still held. Waiting for the result - awaited or blocked on, it
        // makes no difference - used to wait for a lock its own caller held. Deferred dispatch
        // delivers the event only after the lock is released.
        channels["/topic/trigger"] = async (_, _) =>
            await poller!.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/from-handler"] = Ignore
            });

        await using var created = new CometDPoller(
            http, new CometdPollerOptions(channels, "cometd"), NoAuthProvider.Instance, (_, _) => { });
        poller = created;
        await poller.ConnectAsync();

        server.PiggybackOnNextSubscribe = ("/topic/trigger", new { go = true });

        // The timeout is what makes this a test: without it a regression does not fail, it hangs
        // the whole run - CI included - with nothing to say which test is stuck.
        await WithTimeoutAsync(
            poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/outer"] = Ignore
            }),
            TimeSpan.FromSeconds(10));

        Assert.Contains("/topic/outer", server.SubscriptionsSnapshot);
        Assert.Contains("/topic/from-handler", server.SubscriptionsSnapshot);
    }

    [Fact]
    public async Task Events_on_a_rejected_subscribe_are_delivered_without_masking_the_rejection()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var delivered = 0;
        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/first"] = On(_ =>
        {
            Interlocked.Increment(ref delivered);
            throw new InvalidOperationException("handler bug");
        });

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(
            http, new CometdPollerOptions(channels, "cometd"), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.RejectSubscribeFor("/topic/denied");
        server.PiggybackOnNextSubscribe = ("/topic/first", new { late = true });

        // The event is delivered from the finally block, after the rejection is already on its way
        // out. A handler exception escaping there would replace the BayeuxSubscriptionException the
        // caller is owed, and the caller would believe the request itself had failed.
        await Assert.ThrowsAsync<BayeuxSubscriptionException>(
            () => poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/denied"] = Ignore
            }));

        // The server sent it, so it is delivered even though the request failed.
        Assert.Equal(1, delivered);
        Assert.Contains(errors, e => e.Source == ErrorSource.Handler);
    }

    /// <summary>A scriptable extension: each test decides what it does in each direction.</summary>
    /// <remarks>
    /// <c>set</c> rather than <c>init</c>: init accessors need IsExternalInit, which .NET Framework
    /// lacks, and this suite also builds for net472.
    /// </remarks>
    private sealed class TestExtension : IBayeuxExt
    {
        public Func<BaseLongPollingRequestModel, CancellationToken, Task>? OnOutgoing { get; set; }
        public Action<BayeuxResponseMessageModel>? OnIncoming { get; set; }

        public Task OutgoingAsync(BaseLongPollingRequestModel requestModel, CancellationToken cancellationToken) =>
            OnOutgoing?.Invoke(requestModel, cancellationToken) ?? Task.CompletedTask;

        public void Incoming(BayeuxResponseMessageModel responseModel) =>
            OnIncoming?.Invoke(responseModel);
    }

    private static CometdPollerOptions OptionsWith(
        IBayeuxExt extension, string channel, BayeuxEventHandler? handler = null) =>
        new(
            new Dictionary<string, BayeuxEventHandler> { [channel] = handler ?? Ignore },
            "cometd",
            extensions: [extension]);

    [Fact]
    public async Task An_outgoing_extension_writes_ext_that_reaches_the_server()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var extension = new TestExtension
        {
            OnOutgoing = (message, _) =>
            {
                if (message is HandshakeRequestModel)
                    (message.Ext ??= new Dictionary<string, object>())["probe"] = "out";

                return Task.CompletedTask;
            }
        };

        await using var poller = new CometDPoller(
            http, OptionsWith(extension, "/topic/a"), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // Written before serialisation, so it is in the body that went over the wire.
        Assert.Contains("\"ext\":{\"probe\":\"out\"}", server.BodyFor("handshake"));
    }

    [Fact]
    public async Task An_incoming_extension_reads_the_handshake_reply_before_the_subscribe_goes_out()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        server.HandshakeExtJson = """{"probe":true}""";

        var agreed = false;

        // The shape of every negotiated extension: ask in the handshake, act only on a yes. Setup
        // sends the subscribe immediately after the handshake returns, so this only works if the
        // reply reached Incoming before PostCometdAsync handed it back.
        var extension = new TestExtension
        {
            OnIncoming = message =>
            {
                if (message.Channel == "/meta/handshake")
                    agreed = message.Ext is { } ext && ext.ContainsKey("probe");
            },
            OnOutgoing = (message, _) =>
            {
                if (message is SubscribeRequestModel && agreed)
                    (message.Ext ??= new Dictionary<string, object>())["probe"] = "agreed";

                return Task.CompletedTask;
            }
        };

        await using var poller = new CometDPoller(
            http, OptionsWith(extension, "/topic/a"), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        Assert.Contains("\"ext\":{\"probe\":\"agreed\"}", server.BodyFor("subscribe"));
    }

    [Fact]
    public async Task An_incoming_extension_sees_an_event_before_its_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var order = new ConcurrentQueue<string>();

        var extension = new TestExtension
        {
            OnIncoming = message =>
            {
                if (message.Channel == "/topic/a")
                    order.Enqueue("extension");
            }
        };

        await using var poller = new CometDPoller(
            http,
            OptionsWith(extension, "/topic/a", On(_ => order.Enqueue("handler"))),
            NoAuthProvider.Instance,
            (_, _) => { });
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });

        // Replay depends on this: the id is recorded even if the handler then throws.
        Assert.True(await WaitForAsync(() => order.Count == 2));
        Assert.Equal(["extension", "handler"], order.ToArray());
    }

    [Fact]
    public async Task A_failing_incoming_extension_is_reported_and_the_message_is_still_delivered()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var delivered = new ConcurrentQueue<JsonElement>();
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        var extension = new TestExtension
        {
            OnIncoming = message =>
            {
                if (message.Channel == "/topic/a")
                    throw new InvalidOperationException("extension bug");
            }
        };

        await using var poller = new CometDPoller(
            http,
            OptionsWith(extension, "/topic/a", On(data => delivered.Enqueue(data))),
            NoAuthProvider.Instance,
            (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });

        // The message already arrived; one broken extension must not deny it to the handler.
        Assert.True(await WaitForAsync(() => delivered.Count == 1));
        Assert.Contains(errors, e => e.Source == ErrorSource.Ext && !e.IsFatal);
    }

    [Fact]
    public async Task A_failing_outgoing_extension_stops_the_message_and_is_reported_once()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        var extension = new TestExtension
        {
            OnOutgoing = (message, _) => message is HandshakeRequestModel
                ? throw new InvalidOperationException("could not obtain a token")
                : Task.CompletedTask
        };

        await using var poller = new CometDPoller(
            http, OptionsWith(extension, "/topic/a"), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);

        // A message the extension could not prepare must not go out: without the token it failed to
        // obtain, the server would reject it anyway - or, worse, accept it without the extension.
        await Assert.ThrowsAnyAsync<Exception>(() => poller.ConnectAsync());
        Assert.Equal(0, server.HandshakeCount);

        // Once, and as what it is. A call placed inside the transport's try block would also be
        // reported a second time, as an HTTP failure that never happened.
        Assert.Single(errors);
        Assert.Equal(ErrorSource.Ext, errors.Single().Source);
    }

    private static string LastSubscribeBody(FakeBayeuxServer server) =>
        server.RequestBodiesSnapshot.Last(b => b.StartsWith("/cometd/subscribe", StringComparison.Ordinal));

    [Fact]
    public async Task Salesforce_replay_resubscribes_from_the_last_event_after_a_lost_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        server.HandshakeExtJson = """{"replay":true}""";      // the server agrees to replay

        var received = new ConcurrentQueue<JsonElement>();
        var replay = new SalesforceReplayExtension();

        await using var poller = new CometDPoller(
            http,
            OptionsWith(replay, "/event/Low_Ink__e", On(data => received.Enqueue(data))),
            NoAuthProvider.Instance,
            (_, _) => { });
        await poller.ConnectAsync();

        // Nothing seen yet: resume from -1, only new events.
        Assert.True(replay.IsSupported);
        Assert.Contains("\"ext\":{\"replay\":{\"/event/Low_Ink__e\":-1}}", LastSubscribeBody(server));

        // An event carries its position in data.event.replayId.
        server.Publish("/event/Low_Ink__e", new { payload = new { ink = 0.2 }, @event = new { replayId = 2113 } });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        // The session is lost; the poller re-handshakes and re-subscribes on its own.
        server.FailNextConnectsWith402 = 1;
        Assert.True(await WaitForAsync(() => server.HandshakeCount == 2
            && server.SubscriptionsSnapshot.Count(c => c == "/event/Low_Ink__e") == 2));

        // The whole point: the resubscribe asks for everything after the last event seen, so what was
        // published during the gap is not lost.
        Assert.Contains("\"ext\":{\"replay\":{\"/event/Low_Ink__e\":2113}}", LastSubscribeBody(server));
    }

    [Fact]
    public async Task Salesforce_replay_stays_silent_when_the_server_does_not_agree()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        // No ext in the handshake reply: the server never said yes.
        var replay = new SalesforceReplayExtension();

        await using var poller = new CometDPoller(
            http, OptionsWith(replay, "/event/Low_Ink__e"), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // Asked in the handshake, but without agreement nothing more is sent.
        Assert.Contains("\"ext\":{\"replay\":true}", server.BodyFor("handshake"));
        Assert.False(replay.IsSupported);
        Assert.DoesNotContain("\"ext\"", LastSubscribeBody(server));
    }

    private static CometdPollerOptions OptionsFor(string channel, BayeuxEventHandler handler) =>
        new(new Dictionary<string, BayeuxEventHandler> { [channel] = handler }, "cometd");

    [Fact]
    public async Task A_handler_is_awaited_before_the_next_event_is_delivered()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var release = new TaskCompletionSource<bool>();
        var seen = new ConcurrentQueue<int>();

        await using var poller = new CometDPoller(http, OptionsFor("/topic/a", async (e, _) =>
        {
            var n = e.Data.GetProperty("n").GetInt32();
            seen.Enqueue(n);

            if (n == 1)
                await release.Task;
        }), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => seen.Count == 1));

        // The first handler has not finished, so the next long poll has not gone out and the second
        // event is still queued on the server. That is the contract - and why a slow handler delays
        // everything behind it.
        server.Publish("/topic/a", new { n = 2 });
        Assert.False(await WaitForAsync(() => seen.Count == 2, 1000));

        release.SetResult(true);
        Assert.True(await WaitForAsync(() => seen.Count == 2));
        Assert.Equal([1, 2], seen.ToArray());
    }

    [Fact]
    public async Task An_exception_after_an_await_is_reported_and_the_poller_keeps_running()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var calls = 0;
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        // With the old EventHandler signature this lambda was async void: the exception after the
        // await escaped every try block and took the process down.
        await using var poller = new CometDPoller(http, OptionsFor("/topic/a", async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Yield();
            throw new InvalidOperationException("handler bug");
        }), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => errors.Any(e => e.Source == ErrorSource.Handler && !e.IsFatal)));

        server.Publish("/topic/a", new { n = 2 });
        Assert.True(await WaitForAsync(() => calls == 2));
    }

    [Fact]
    public async Task A_wildcard_handler_is_told_the_concrete_channel()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var channels = new ConcurrentQueue<string>();

        await using var poller = new CometDPoller(http, OptionsFor("/topic/**", (e, _) =>
        {
            channels.Enqueue(e.Channel);
            return Task.CompletedTask;
        }), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.Publish("/topic/orders/17", new { n = 1 });
        server.Publish("/topic/alerts", new { n = 2 });

        // Without the channel a pattern subscription could not tell its events apart.
        Assert.True(await WaitForAsync(() => channels.Count == 2));
        Assert.Equal(["/topic/orders/17", "/topic/alerts"], channels.ToArray());
    }

    [Fact]
    public async Task Disconnecting_cancels_the_handlers_token_without_reporting_an_error()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var started = new TaskCompletionSource<bool>();
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, OptionsFor("/topic/a", async (_, token) =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
        }), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });
        await WithTimeoutAsync(started.Task, TimeSpan.FromSeconds(10));

        // DisconnectAsync waits for the handler, so a handler that ignored the token would hang it.
        await WithTimeoutAsync(poller.DisconnectAsync(), TimeSpan.FromSeconds(10));

        // A handler stopping because it was told to is not a failure.
        Assert.DoesNotContain(errors, e => e.Source == ErrorSource.Handler);
    }

    [Fact]
    public async Task A_handler_may_await_DisconnectAsync_without_deadlocking()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        CometDPoller? poller = null;
        var returned = new TaskCompletionSource<bool>();
        var stopped = new TaskCompletionSource<bool>();

        // Disposed with Dispose, not DisposeAsync: if this regresses, the loop is deadlocked, and
        // DisposeAsync - which waits for the loop - would hang the run instead of failing the test.
        using var created = new CometDPoller(http, OptionsFor("/control/stop", async (_, _) =>
        {
            // The loop is waiting for this handler, and DisconnectAsync normally waits for the loop.
            // From inside a handler it only signals, and the loop stops once the handler returns.
            await poller!.DisconnectAsync();
            returned.TrySetResult(true);
        }), NoAuthProvider.Instance, (_, _) => stopped.TrySetResult(true));
        poller = created;
        await poller.ConnectAsync();

        server.Publish("/control/stop", new { n = 1 });

        await WithTimeoutAsync(returned.Task, TimeSpan.FromSeconds(10));
        await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_handler_cannot_restart_the_poller_and_is_told_where_to_do_it()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        CometDPoller? poller = null;
        var refused = new TaskCompletionSource<Exception>();

        // Disposed with Dispose, not DisposeAsync: if this regresses, the loop is deadlocked, and
        // DisposeAsync - which waits for the loop - would hang the run instead of failing the test.
        using var created = new CometDPoller(http, OptionsFor("/control/restart", async (_, _) =>
        {
            try
            {
                await poller!.ConnectAsync();
            }
            catch (Exception ex)
            {
                refused.TrySetResult(ex);
            }
        }), NoAuthProvider.Instance, (_, _) => { });
        poller = created;
        await poller.ConnectAsync();

        server.Publish("/control/restart", new { n = 1 });

        // Refused rather than hung: restarting means waiting for the loop that is waiting for this
        // handler. The message names the way that does work.
        var error = await WithTimeoutAsync(refused.Task, TimeSpan.FromSeconds(10));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("OnPollerDisconnected", error.Message);
    }

    [Fact]
    public async Task OnPollerDisconnected_may_reconnect_after_handlers_have_run()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        CometDPoller? poller = null;
        var reconnected = new TaskCompletionSource<Exception?>();
        var first = true;
        var received = new ConcurrentQueue<JsonElement>();

        await using var created = new CometDPoller(
            http,
            OptionsFor("/topic/a", On(data => received.Enqueue(data))),
            NoAuthProvider.Instance,
            async (_, _) =>
            {
                if (!first)
                    return;

                first = false;

                try
                {
                    await poller!.ConnectAsync();
                    reconnected.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    reconnected.TrySetResult(ex);
                }
            });
        poller = created;
        await poller.ConnectAsync();

        // A handler has run, so a "call came from a handler" flag that leaked out of the dispatch
        // would now wrongly refuse the documented way to reconnect.
        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        server.StopOnNextConnect = true;

        Assert.Null(await WithTimeoutAsync(reconnected.Task, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_handler_cannot_change_ext_for_the_handlers_after_it()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();
        var laterSawKey = new TaskCompletionSource<bool>();

        // Two overlapping subscriptions, so one event reaches two handlers through the same
        // BayeuxEvent. The first tries to strip ext; the second must still see what the server sent.
        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/**"] = (e, _) =>
            {
                // IReadOnlyDictionary hides the mutating methods from the compiler only; the object
                // behind it must not be a writable dictionary either.
                Assert.False(e.Ext is Dictionary<string, JsonElement>);
                ((IDictionary<string, JsonElement>)e.Ext!).Remove("probe");
                return Task.CompletedTask;
            },
            ["/topic/orders"] = (e, _) =>
            {
                laterSawKey.TrySetResult(e.Ext!.ContainsKey("probe"));
                return Task.CompletedTask;
            }
        };

        await using var poller = new CometDPoller(
            http, new CometdPollerOptions(channels, "cometd"), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.Publish("/topic/orders", new { n = 1 }, extJson: """{"probe":true}""");

        Assert.True(await WithTimeoutAsync(laterSawKey.Task, TimeSpan.FromSeconds(10)));

        // The attempt fails loudly - NotSupportedException from the wrapper - and is reported like
        // any other handler exception.
        Assert.Contains(errors, e => e.Source == ErrorSource.Handler && e.Error is NotSupportedException);
    }

    [Fact]
    public async Task Connecting_again_while_connected_replaces_the_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var received = new ConcurrentQueue<JsonElement>();
        var disconnects = new ConcurrentQueue<OnPollerDisconnectedEventArgs>();

        await using var poller = new CometDPoller(
            http, OptionsFor("/topic/a", On(data => received.Enqueue(data))), NoAuthProvider.Instance,
            (_, e) => disconnects.Enqueue(e));
        await poller.ConnectAsync();

        // ConnectAsync stops the running session first. The token it waits with must not be the
        // session's own - stopping the session would cancel it and fail the call nobody cancelled.
        await WithTimeoutAsync(poller.ConnectAsync(), TimeSpan.FromSeconds(10));

        Assert.Equal(2, server.HandshakeCount);
        Assert.Equal(DisconnectReason.TokenCancellation, Assert.Single(disconnects).Reason);

        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task Connecting_with_a_cancelled_token_sends_nothing_and_leaves_the_poller_usable()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var received = new ConcurrentQueue<JsonElement>();

        await using var poller = new CometDPoller(
            http, OptionsFor("/topic/a", On(data => received.Enqueue(data))), NoAuthProvider.Instance, (_, _) => { });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Thrown, not returned: a connect that "succeeds" without starting the loop leaves a
        // session on the server and a poller that never delivers anything.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.ConnectAsync(cancelled.Token));
        Assert.Equal(0, server.HandshakeCount);
        Assert.Equal(string.Empty, poller.ClientId);

        await poller.ConnectAsync();
        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task A_connect_token_does_not_govern_the_running_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var received = new ConcurrentQueue<JsonElement>();
        var stopped = false;

        await using var poller = new CometDPoller(
            http, OptionsFor("/topic/a", On(data => received.Enqueue(data))), NoAuthProvider.Instance,
            (_, _) => stopped = true);

        // "Give up if connecting takes too long" - and then, typically, disposed with the using.
        using (var connectTimeout = new CancellationTokenSource())
        {
            await poller.ConnectAsync(connectTimeout.Token);
            connectTimeout.Cancel();
        }

        // The token bounded the connect. The session outlives it.
        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.False(stopped);
    }

    [Fact]
    public async Task Subscribing_with_a_cancelled_token_subscribes_nothing()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/a");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.SubscribeNewChannelsAsync(
            new Dictionary<string, BayeuxEventHandler> { ["/topic/b"] = Ignore }, cancelled.Token));

        // Neither side changed: not sent, and not registered locally.
        Assert.DoesNotContain("/topic/b", server.SubscriptionsSnapshot);
        Assert.DoesNotContain("/topic/b", options.Channels.Keys);
    }

    [Fact]
    public async Task Unsubscribing_with_a_cancelled_token_keeps_every_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/a");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => poller.UnsubscribeChannelsAsync(["/topic/a"], cancelled.Token));

        Assert.Empty(server.UnsubscriptionsSnapshot);
        Assert.Contains("/topic/a", options.Channels.Keys);

        // Still subscribed on the server, so the handler must still be there to receive.
        server.Publish("/topic/a", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task A_disconnect_token_bounds_the_wait_but_not_the_stop()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var started = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var stopped = new TaskCompletionSource<OnPollerDisconnectedEventArgs>();

        // Disposed with Dispose, not DisposeAsync: if the stop regressed, the loop would never end
        // and DisposeAsync would hang the run instead of failing the test.
        using var poller = new CometDPoller(http, OptionsFor("/topic/a", async (_, _) =>
        {
            started.TrySetResult(true);
            await release.Task;                     // ignores its token on purpose
        }), NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await poller.ConnectAsync();

        server.Publish("/topic/a", new { n = 1 });
        await WithTimeoutAsync(started.Task, TimeSpan.FromSeconds(10));

        // The caller is not willing to wait for a handler that will not stop: the wait gives up...
        using (var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.DisconnectAsync(patience.Token));

        // ...but the stop was already decided, and happens as soon as the handler lets it.
        Assert.False(stopped.Task.IsCompleted);
        release.SetResult(true);

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));
        Assert.Equal(DisconnectReason.TokenCancellation, outcome.Reason);
    }

    [Fact]
    public async Task An_extension_cancelled_by_a_stop_is_not_reported_as_a_failure()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var armed = false;
        var waiting = new TaskCompletionSource<bool>();
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();
        var stopped = new TaskCompletionSource<OnPollerDisconnectedEventArgs>();

        // Does what an extension should: passes the token on to what it awaits.
        var extension = new TestExtension
        {
            OnOutgoing = async (message, token) =>
            {
                if (armed && message is ConnectRequestModel)
                {
                    waiting.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token);
                }
            }
        };

        await using var poller = new CometDPoller(
            http, OptionsWith(extension, "/topic/a"), NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        armed = true;
        server.Publish("/topic/a", new { n = 1 });            // ends the held poll; the next goes through the extension
        await WithTimeoutAsync(waiting.Task, TimeSpan.FromSeconds(10));

        await WithTimeoutAsync(poller.DisconnectAsync(), TimeSpan.FromSeconds(10));

        // The extension stopped because it was told to. Reporting that as a fatal extension failure
        // would raise a false alarm on every ordinary shutdown that caught an extension mid-I/O.
        Assert.Equal(DisconnectReason.TokenCancellation, (await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10))).Reason);
        Assert.DoesNotContain(errors, e => e.Source == ErrorSource.Ext);
    }
}
