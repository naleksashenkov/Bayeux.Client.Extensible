// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary><see cref="CometDPoller.PublishAsync"/>: what goes out, and what a reply may contain.</summary>
/// <remarks>Part of <see cref="CometDPollerTests"/> to share its helpers.</remarks>
public partial class CometDPollerTests
{
    // The request body the fake server recorded for the one publish a test made.
    private static JsonElement LastPublishRequest(FakeBayeuxServer server)
    {
        var body = server.RequestBodiesSnapshot.Last(b => b.StartsWith("/cometd ", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(body.Substring("/cometd ".Length));
        return document.RootElement[0].Clone();
    }

    [Fact]
    public async Task A_publish_goes_to_the_base_path_in_the_bayeux_wire_format()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.PublishAsync("/chat/room1", new { text = "hi" });

        // The base path, with no type appended: only meta messages carry one.
        var message = LastPublishRequest(server);

        Assert.Equal("/chat/room1", message.GetProperty("channel").GetString());
        Assert.Equal("hi", message.GetProperty("data").GetProperty("text").GetString());
        Assert.Equal(poller.ClientId, message.GetProperty("clientId").GetString());
        Assert.False(string.IsNullOrEmpty(message.GetProperty("id").GetString()));
        Assert.False(message.TryGetProperty("ext", out _));
    }

    [Fact]
    public async Task The_acknowledgement_is_not_delivered_to_handlers()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var received = new ConcurrentQueue<JsonElement>();

        await using var poller = new CometDPoller(
            http, OptionsFor("/chat/**", On(data => received.Enqueue(data))), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.PublishAsync("/chat/room1", new { text = "hi" });

        // The broadcast arrives once, as an event. The reply to the publish - same channel,
        // "successful" and no data - is not an event, and must not reach the handler as one
        // with empty data.
        Assert.True(await WaitForAsync(() => received.Count == 1));
        await Task.Delay(300);

        var only = Assert.Single(received);
        Assert.Equal("hi", only.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_subscriber_receives_its_own_message_even_inside_the_publish_reply()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/chat/room1");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // CometD delivers to every subscriber, the publisher included, and may do it in the very
        // response to the publish. Documented behaviour, not something to filter out.
        server.EchoPublishesInReply = true;

        await poller.PublishAsync("/chat/room1", new { text = "echo" });

        // Delivered before PublishAsync returned: events in a reply are handled before the
        // reply itself is.
        var only = Assert.Single(received);
        Assert.Equal("echo", only.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_refused_publish_throws_with_the_servers_reason()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        var raised = false;

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        await poller.ConnectAsync();

        server.RejectPublishFor("/chat/closed");

        var refused = await Assert.ThrowsAsync<BayeuxPublishException>(
            () => poller.PublishAsync("/chat/closed", new { text = "hi" }));

        Assert.Equal("/chat/closed", refused.Channel);
        Assert.Equal("403:/chat/closed:Publish denied", refused.Error);

        // A refused message is the caller's problem, not the session's.
        Assert.False(raised);
        Assert.False(string.IsNullOrEmpty(poller.ClientId));
    }

    [Fact]
    public async Task A_transport_failure_is_thrown_to_the_caller_and_not_reported()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        var raised = false;
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.NextPublishHttpStatus = 503;

        var failed = await Assert.ThrowsAsync<BayeuxHttpException>(() => poller.PublishAsync("/chat/room1", new { }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        // Once, as an exception: reporting it through OnError as well would announce one failure
        // twice. And the session carries on.
        Assert.DoesNotContain(errors, e => e.Source == ErrorSource.Http);
        Assert.False(raised);
    }

    [Fact]
    public async Task A_publish_the_server_never_answers_is_a_protocol_error()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.OmitNextPublishReply = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PublishAsync("/chat/room1", new { }));
    }

    [Fact]
    public async Task Invalid_channels_are_refused_before_anything_is_sent()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // Missing, unrooted, reserved for the protocol, and wildcards - which name sets of
        // channels to subscribe to, while a message goes to exactly one.
        foreach (var channel in new[] { null!, "", "chat/room1", "/meta/connect", "/chat/*", "/chat/**" })
            await Assert.ThrowsAsync<ArgumentException>("channel", () => poller.PublishAsync(channel, new { }));

        Assert.Empty(server.PublishesSnapshot);

        // A service channel is a message to the server itself, and allowed.
        await poller.PublishAsync("/service/echo", new { });
        Assert.Single(server.PublishesSnapshot);
    }

    [Fact]
    public async Task Publishing_without_a_session_throws_and_sends_nothing()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PublishAsync("/chat/room1", new { }));
        Assert.Empty(server.PublishesSnapshot);

        poller.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => poller.PublishAsync("/chat/room1", new { }));
    }

    [Fact]
    public async Task A_handler_may_publish()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IBayeuxPoller? poller = null;

        // Publishing takes no lock, so awaiting it from a handler - the loop waiting on that very
        // handler - cannot deadlock the way a blocking subscribe once did.
        var options = OptionsFor("/topic/in", async (e, token) =>
        {
            await poller!.PublishAsync("/topic/out", new { reply = true }, token);
            published.TrySetResult(true);
        });

        await using var created = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        poller = created;
        created.OnError += (_, e) => errors.Enqueue(e);
        await created.ConnectAsync();

        server.Publish("/topic/in", new { n = 1 });

        Assert.True(await WithTimeoutAsync(published.Task, TimeSpan.FromSeconds(10)));
        Assert.Equal("/topic/out", Assert.Single(server.PublishesSnapshot)["channel"]!.GetValue<string>());
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Extensions_see_a_publish_like_any_other_message()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var extension = new TestExtension
        {
            OnOutgoing = (message, _) =>
            {
                if (message is PublishRequestModel)
                    message.Ext = new Dictionary<string, object> { ["signature"] = "abc" };

                return Task.CompletedTask;
            }
        };

        await using var poller = new CometDPoller(
            http, OptionsWith(extension, "/topic/unrelated"), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.PublishAsync("/chat/room1", new { text = "hi" });

        Assert.Equal("abc", LastPublishRequest(server).GetProperty("ext").GetProperty("signature").GetString());
    }
}
