// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// <see cref="BayeuxTransportType.WebSocket"/>: one socket for the session, replies matched by id,
/// events pushed as they come.
/// </summary>
/// <remarks>Part of <see cref="BayeuxClientTests"/> to share its helpers.</remarks>
public partial class BayeuxClientTests
{
    private static (BayeuxClientOptions options, ConcurrentQueue<JsonElement> received) WebSocketOptions(
        string channel, ReconnectOptions? reconnectOptions = null)
    {
        var received = new ConcurrentQueue<JsonElement>();

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { [channel] = On(data => received.Enqueue(data)) },
            "cometd",
            reconnectOptions: reconnectOptions,
            transportType: BayeuxTransportType.WebSocket);

        return (options, received);
    }

    // Short enough for a test to watch a dead socket being found; the server holds a connect for
    // AdviceTimeoutMs, advised in the handshake only, as CometD does.
    private static (BayeuxClientOptions options, ConcurrentQueue<JsonElement> received) DeadSocketOptions(string channel)
    {
        var received = new ConcurrentQueue<JsonElement>();

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { [channel] = On(data => received.Enqueue(data)) },
            "cometd",
            reconnectOptions: new ReconnectOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)),
            transportType: BayeuxTransportType.WebSocket,
            maxNetworkDelay: TimeSpan.FromMilliseconds(400));

        return (options, received);
    }

    [Fact]
    public async Task The_whole_session_runs_over_one_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/ws", new { n = 1 });

        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // One upgrade, and not a single long-polling request beside it.
        Assert.Equal(1, server.WebSocketUpgrades);
        Assert.Empty(server.RequestBodiesSnapshot);

        // The transport is named on the wire, as the server needs it to be.
        var frames = server.WebSocketFramesSnapshot;
        Assert.Contains(frames, f => f.Contains("\"supportedConnectionTypes\":[\"websocket\"]"));
        Assert.Contains(frames, f => f.Contains("\"connectionType\":\"websocket\""));
    }

    [Fact]
    public async Task An_event_arrives_while_the_connect_is_still_held()
    {
        // Long enough that, were events still waiting for a connect reply, the test would time out.
        using var server = new FakeBayeuxServer { WebSocketConnectHold = TimeSpan.FromSeconds(30) };
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        Assert.True(await WaitForAsync(() => server.WebSocketFramesSnapshot.Any(f => f.Contains("/meta/connect"))));

        server.Publish("/topic/ws", new { n = 1 });

        Assert.True(await WaitForAsync(() => received.Count == 1, timeoutMs: 3000));
    }

    [Fact]
    public async Task Events_reach_the_handler_in_the_order_the_server_sent_them()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var received = new ConcurrentQueue<int>();

        // A handler slow on some events: were batches dispatched side by side, a later batch would
        // overtake the one still waiting on it.
        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler>
            {
                ["/topic/order"] = async (e, _) =>
                {
                    var n = e.Data.GetProperty("n").GetInt32();
                    if (n % 7 == 0)
                        await Task.Delay(20);
                    received.Enqueue(n);
                }
            },
            "cometd",
            transportType: BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        // In bursts, so they reach the client in several frames.
        for (var n = 0; n < 60; n++)
        {
            server.Publish("/topic/order", new { n });

            if (n % 10 == 9)
                await Task.Delay(30);
        }

        Assert.True(await WaitForAsync(() => received.Count == 60, timeoutMs: 10000));
        Assert.Equal(Enumerable.Range(0, 60), received);
    }

    [Fact]
    public async Task Credentials_and_cookies_go_out_once_in_the_upgrade()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        var cookies = new CookieContainer();
        cookies.Add(new Uri(server.Prefix), new Cookie("session", "abc"));

        await using var client = new BayeuxClient(
            http, options, new HttpBasicAuthProvider(new BasicAuthCredentials("user", "secret")), (_, _) => { }, cookies);
        await client.ConnectAsync();

        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        // The upgrade is the only HTTP request; every Bayeux message after it rides the socket.
        var auth = Assert.Single(server.AuthHeadersSnapshot);
        Assert.StartsWith("Basic ", auth);
        Assert.Contains("session=abc", Assert.Single(server.CookieHeadersSnapshot));

        // And what the server set on the upgrade response is kept, in the client's own store.
        Assert.Contains("BAYEUX_UPGRADE=1", cookies.GetCookieHeader(new Uri(server.Prefix)));
    }

    [Fact]
    public async Task A_publish_is_answered_on_the_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/chat/room1");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        await client.PublishAsync("/chat/room1", new { text = "hi" });

        // Delivered back to the subscribed publisher, as a pushed event.
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal("hi", Assert.Single(received).GetProperty("text").GetString());

        server.RejectPublishFor("/chat/closed");

        var refused = await Assert.ThrowsAsync<BayeuxPublishException>(
            () => client.PublishAsync("/chat/closed", new { text = "hi" }));
        Assert.Equal("403:/chat/closed:Publish denied", refused.Error);

        Assert.Equal(1, server.WebSocketUpgrades);
    }

    [Fact]
    public async Task Cancelling_a_wait_leaves_the_socket_alone()
    {
        using var server = new FakeBayeuxServer { PublishReplyDelay = TimeSpan.FromSeconds(2) };
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.PublishAsync("/topic/other", new { }, cts.Token));

        // Cancelling a ClientWebSocket operation itself would have aborted the socket. Only the
        // wait was cancelled: the same socket still carries requests and events.
        server.PublishReplyDelay = TimeSpan.Zero;
        await client.PublishAsync("/topic/other", new { });

        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(1, server.WebSocketUpgrades);
        Assert.Equal(1, server.OpenWebSocketCount);
    }

    [Fact]
    public async Task A_dropped_socket_is_replaced_and_the_session_kept()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        // The default policy: a dropped socket is a transport failure like a lost HTTP connection.
        var (options, received) = WebSocketOptions(
            "/topic/ws",
            new ReconnectOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));

        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        var session = client.ClientId;

        server.AbortWebSockets();

        // A ClientWebSocket cannot reconnect; the next send opens a new one.
        Assert.True(await WaitForAsync(() => server.WebSocketUpgrades == 2));
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // Over it, the same session: no new handshake, so nothing queued on the server is lost.
        Assert.Equal(1, server.HandshakeCount);
        Assert.Equal(session, client.ClientId);
        Assert.Contains(errors, e => e.Source == ErrorSource.Transport && e.Error is WebSocketException && !e.IsFatal);

        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task A_server_without_websocket_refuses_the_upgrade()
    {
        using var server = new FakeBayeuxServer { WebSocketEnabled = false };
        using var http = server.CreateClient();
        var (options, _) = WebSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        await Assert.ThrowsAnyAsync<WebSocketException>(() => client.ConnectAsync());
        Assert.Equal(0, server.HandshakeCount);
    }

    [Fact]
    public async Task Disposing_disconnects_and_then_closes_the_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = WebSocketOptions("/topic/ws");

        var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.Equal(1, server.OpenWebSocketCount);

        await client.DisposeAsync();

        // The disconnect went over the socket, which was open for it, and was closed after.
        Assert.Equal(1, server.DisconnectCount);
        Assert.True(await WaitForAsync(() => server.OpenWebSocketCount == 0));
    }

    [Fact]
    public async Task Dispose_without_waiting_still_disconnects_and_closes_the_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = WebSocketOptions("/topic/ws");

        var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        // The loop finishes on its own: it sends the disconnect, then closes the transport.
        client.Dispose();

        Assert.True(await WaitForAsync(() => server.DisconnectCount == 1));
        Assert.True(await WaitForAsync(() => server.OpenWebSocketCount == 0));
    }

    [Fact]
    public async Task Dispose_after_the_client_stopped_closes_the_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = WebSocketOptions("/topic/ws");

        var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        await client.DisconnectAsync();

        // Stopped, not disposed: the socket stays for a ConnectAsync that may follow.
        Assert.Equal(1, server.OpenWebSocketCount);

        // No loop is left to close it, so Dispose does.
        client.Dispose();

        Assert.True(await WaitForAsync(() => server.OpenWebSocketCount == 0));
    }

    [Fact]
    public async Task A_stopped_client_connects_again()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        await client.DisconnectAsync();

        // The transport outlives a session: stopping must not dispose it.
        await client.ConnectAsync();

        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(2, server.HandshakeCount);
    }

    [Fact]
    public async Task A_socket_that_goes_silent_is_replaced_and_the_session_kept()
    {
        using var server = new FakeBayeuxServer
        {
            AdviceTimeoutMs = 600,
            WebSocketConnectHold = TimeSpan.FromMilliseconds(600)
        };
        using var http = server.CreateClient();
        var (options, received) = DeadSocketOptions("/topic/ws");

        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        var session = client.ClientId;

        // No error, no close: the socket simply stops answering. Only the missing reply tells -
        // after the server's hold plus the network delay.
        server.MuteWebSockets();
        server.Publish("/topic/ws", new { n = 1 });

        Assert.True(await WaitForAsync(() => server.WebSocketUpgrades == 2));
        Assert.Contains(errors, e => e.Error is WebSocketException && !e.IsFatal);

        // The same session over the new socket, and what was queued in it meanwhile arrives.
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(1, server.HandshakeCount);
        Assert.Equal(session, client.ClientId);
    }

    [Fact]
    public async Task A_connect_held_for_the_advised_time_is_not_taken_for_a_dead_socket()
    {
        // The hold is the whole advised timeout, and the margin is short: a limit that forgot
        // either part, or the advice from the handshake, would abort every healthy connect.
        using var server = new FakeBayeuxServer
        {
            AdviceTimeoutMs = 1000,
            WebSocketConnectHold = TimeSpan.FromMilliseconds(1000)
        };
        using var http = server.CreateClient();
        var (options, received) = DeadSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        // Several held connects in a row.
        await Task.Delay(3500);

        Assert.Equal(1, server.WebSocketUpgrades);
        Assert.Equal(BayeuxClientState.Connected, client.State);

        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task A_slow_reply_within_the_limit_keeps_the_socket()
    {
        using var server = new FakeBayeuxServer { PublishReplyDelay = TimeSpan.FromMilliseconds(200) };
        using var http = server.CreateClient();
        var (options, _) = DeadSocketOptions("/topic/ws");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        await client.PublishAsync("/topic/other", new { });

        // Past the limit, though, the publish fails - and so does the socket under it.
        server.PublishReplyDelay = TimeSpan.FromSeconds(2);

        var failed = await Assert.ThrowsAsync<WebSocketException>(() => client.PublishAsync("/topic/other", new { }));
        Assert.Contains("presumed dead", failed.Message);
        Assert.True(await WaitForAsync(() => server.WebSocketUpgrades == 2));
    }

    // A client whose held connect would outlast the test: it can only stop in time if the server's
    // disconnect calls off the wait. Returns the reason it stopped with.
    private static (BayeuxClient client, Task<DisconnectReason> stopped) HeldClient(
        FakeBayeuxServer server, HttpClient http, BayeuxClientOptions options)
    {
        var stopped = new TaskCompletionSource<DisconnectReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e.Reason));
        return (client, stopped.Task);
    }

    [Fact]
    public async Task The_server_ending_the_session_stops_the_client_at_once()
    {
        using var server = new FakeBayeuxServer { WebSocketConnectHold = TimeSpan.FromSeconds(30) };
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        var (client, stopped) = HeldClient(server, http, options);
        await using var disposal = client;
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => server.WebSocketFramesSnapshot.Any(f => f.Contains("/meta/connect"))));

        // An event first, then the goodbye: the event still reaches its handler.
        server.Publish("/topic/ws", new { n = 1 });
        server.PushDisconnect();

        // Not after the 30-second hold: the wait for the connect is called off.
        Assert.Equal(DisconnectReason.ServerRequirement, await WithTimeoutAsync(stopped, TimeSpan.FromSeconds(5)));

        Assert.Single(received);
        Assert.Equal(BayeuxClientState.Disconnected, client.State);
        Assert.Empty(client.ClientId);

        // The session is gone on the server: nothing to say goodbye to, and no new one.
        Assert.Equal(0, server.DisconnectCount);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task The_server_ending_the_session_is_not_retried()
    {
        using var server = new FakeBayeuxServer { WebSocketConnectHold = TimeSpan.FromSeconds(30) };
        using var http = server.CreateClient();
        var (options, _) = WebSocketOptions(
            "/topic/ws",
            new ReconnectOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), shouldRetry: _ => true));

        var (client, stopped) = HeldClient(server, http, options);
        await using var disposal = client;
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => server.WebSocketFramesSnapshot.Any(f => f.Contains("/meta/connect"))));

        server.PushDisconnect();

        // A reconnect policy retries failures; this is the server's decision, not a failure.
        Assert.Equal(DisconnectReason.ServerRequirement, await WithTimeoutAsync(stopped, TimeSpan.FromSeconds(5)));
        await Task.Delay(500);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task A_disconnect_that_is_not_the_servers_own_is_ignored()
    {
        using var server = new FakeBayeuxServer { WebSocketConnectHold = TimeSpan.FromMilliseconds(300) };
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        var (client, stopped) = HeldClient(server, http, options);
        await using var disposal = client;
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // A late reply to a disconnect of the client's own carries its id; a leftover of an older
        // session names that session. Neither ends this one.
        server.PushDisconnect(id: "999");
        server.PushDisconnect(clientId: "cid-old");
        await Task.Delay(1000);

        Assert.False(stopped.IsCompleted);
        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        // The real one does.
        server.PushDisconnect();
        Assert.Equal(DisconnectReason.ServerRequirement, await WithTimeoutAsync(stopped, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_client_the_server_stopped_connects_again()
    {
        using var server = new FakeBayeuxServer { WebSocketConnectHold = TimeSpan.FromMilliseconds(300) };
        using var http = server.CreateClient();
        var (options, received) = WebSocketOptions("/topic/ws");

        var (client, stopped) = HeldClient(server, http, options);
        await using var disposal = client;
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        server.PushDisconnect();
        await WithTimeoutAsync(stopped, TimeSpan.FromSeconds(5));

        // A new session starts clean: the old session's stop must not end it at its first connect.
        await client.ConnectAsync();
        await Task.Delay(1000);

        Assert.Equal(BayeuxClientState.Connected, client.State);
        server.Publish("/topic/ws", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }
}
