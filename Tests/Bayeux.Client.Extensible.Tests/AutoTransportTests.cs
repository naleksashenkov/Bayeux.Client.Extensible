// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// <see cref="BayeuxTransportType.Auto"/>: WebSocket where it gets through, long-polling where it
/// does not - and no going back once WebSocket has proven itself.
/// </summary>
/// <remarks>Part of <see cref="BayeuxClientTests"/> to share its helpers.</remarks>
public partial class BayeuxClientTests
{
    private static (BayeuxClientOptions options, ConcurrentQueue<JsonElement> received) AutoOptions(
        string channel, ReconnectOptions? reconnectOptions = null, TimeSpan? maxNetworkDelay = null)
    {
        var received = new ConcurrentQueue<JsonElement>();

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { [channel] = On(data => received.Enqueue(data)) },
            "cometd",
            reconnectOptions: reconnectOptions,
            transportType: BayeuxTransportType.Auto,
            maxNetworkDelay: maxNetworkDelay);

        return (options, received);
    }

    private static bool PolledOverHttp(FakeBayeuxServer server) =>
        server.RequestBodiesSnapshot.Any(b => b.StartsWith("/cometd/connect", StringComparison.Ordinal));

    private static bool ConnectedOverWebSocket(FakeBayeuxServer server) =>
        server.WebSocketFramesSnapshot.Any(f => f.Contains("/meta/connect"));

    [Fact]
    public async Task Auto_uses_websocket_where_the_server_offers_it()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = AutoOptions("/topic/auto");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(1, server.WebSocketUpgrades);
        Assert.Empty(server.RequestBodiesSnapshot);
        Assert.Equal(BayeuxTransportType.WebSocket, client.ActiveTransportType);

        // It offers both: it can use either.
        Assert.Contains(server.WebSocketFramesSnapshot,
            f => f.Contains("\"supportedConnectionTypes\":[\"websocket\",\"long-polling\"]"));
    }

    [Fact]
    public async Task Auto_falls_back_when_the_upgrade_is_refused()
    {
        using var server = new FakeBayeuxServer { WebSocketEnabled = false };
        using var http = server.CreateClient();
        var (options, received) = AutoOptions("/topic/auto");
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        Assert.True(PolledOverHttp(server));
        Assert.Equal(BayeuxTransportType.LongPolling, client.ActiveTransportType);

        // Recovered from, so not reported: an application would otherwise see a fatal transport
        // error from a client that is working fine.
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Auto_falls_back_when_the_server_does_not_offer_websocket()
    {
        using var server = new FakeBayeuxServer { OfferWebSocket = false };
        using var http = server.CreateClient();
        var (options, received) = AutoOptions("/topic/auto");
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        // The socket carried a handshake the server answered without "websocket"; a second one,
        // over long-polling, opened the session in use.
        Assert.Equal(2, server.HandshakeCount);
        Assert.True(PolledOverHttp(server));
        Assert.Equal(BayeuxTransportType.LongPolling, client.ActiveTransportType);
        Assert.DoesNotContain(errors, e => e.Source == ErrorSource.Configuration);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Auto_falls_back_when_the_socket_carries_the_handshake_but_not_the_connect()
    {
        // A proxy that lets the upgrade through and drops the frames after it. No reconnect policy:
        // falling back is not a retry, and needs none.
        using var server = new FakeBayeuxServer { DropWebSocketConnects = true, AdviceTimeoutMs = 500 };
        using var http = server.CreateClient();
        var (options, received) = AutoOptions("/topic/auto", maxNetworkDelay: TimeSpan.FromMilliseconds(300));
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        Assert.True(await WaitForAsync(() => PolledOverHttp(server)));
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(2, server.HandshakeCount);
        Assert.Equal(BayeuxTransportType.LongPolling, client.ActiveTransportType);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Once_websocket_has_worked_a_dropped_socket_does_not_fall_back()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = AutoOptions(
            "/topic/auto", new ReconnectOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)));

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // A connect has been answered over WebSocket: from here a failure is the network's.
        server.AbortWebSockets();

        Assert.True(await WaitForAsync(() => server.WebSocketUpgrades == 2));
        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.False(PolledOverHttp(server));
        Assert.Equal(1, server.HandshakeCount);
        Assert.Equal(BayeuxTransportType.WebSocket, client.ActiveTransportType);
    }

    [Fact]
    public async Task Each_ConnectAsync_tries_websocket_again()
    {
        using var server = new FakeBayeuxServer { WebSocketEnabled = false };
        using var http = server.CreateClient();
        var (options, received) = AutoOptions("/topic/auto");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => PolledOverHttp(server)));
        await client.DisconnectAsync();

        // Stopped, it still names what it last used.
        Assert.Equal(BayeuxTransportType.LongPolling, client.ActiveTransportType);

        // The proxy is fixed; the client is connected again.
        server.WebSocketEnabled = true;
        await client.ConnectAsync();
        Assert.Equal(BayeuxTransportType.WebSocket, client.ActiveTransportType);

        Assert.True(await WaitForAsync(() => ConnectedOverWebSocket(server)));
        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(1, server.WebSocketUpgrades);
    }

    [Fact]
    public async Task Auto_closes_the_socket_it_fell_back_from()
    {
        // The socket opened and carried a handshake before the server turned out not to offer
        // WebSocket: it stays open until the client is disposed, and must be closed then.
        using var server = new FakeBayeuxServer { OfferWebSocket = false };
        using var http = server.CreateClient();
        var (options, _) = AutoOptions("/topic/auto");

        var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => PolledOverHttp(server)));

        await client.DisposeAsync();

        Assert.True(await WaitForAsync(() => server.OpenWebSocketCount == 0));
    }

    [Theory]
    [InlineData(BayeuxTransportType.LongPolling)]
    [InlineData(BayeuxTransportType.WebSocket)]
    public void A_fixed_transport_is_the_active_one_from_the_start(BayeuxTransportType transport)
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler>(), "cometd", transportType: transport);

        using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        Assert.Equal(transport, client.ActiveTransportType);
    }

    [Fact]
    public void Auto_names_websocket_before_it_has_tried_anything()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = AutoOptions("/topic/auto");

        using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        // What it will try first; never Auto itself.
        Assert.Equal(BayeuxTransportType.WebSocket, client.ActiveTransportType);
    }
}
