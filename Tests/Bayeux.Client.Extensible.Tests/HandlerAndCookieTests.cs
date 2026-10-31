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

/// <summary>
/// The caller's <see cref="HttpClient"/> and a socket opened through it, and cookies a transport
/// is sent but cannot keep.
/// </summary>
/// <remarks>Part of <see cref="BayeuxClientTests"/> to share its helpers.</remarks>
public partial class BayeuxClientTests
{
    private const string CookieForAnotherDomain = "foreign=1; Domain=other.example; Path=/";

    private static (BayeuxClientOptions options, ConcurrentQueue<JsonElement> received) ChannelOptions(
        string channel, BayeuxTransportType transport)
    {
        var received = new ConcurrentQueue<JsonElement>();

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { [channel] = On(data => received.Enqueue(data)) },
            "cometd",
            transportType: transport);

        return (options, received);
    }

    [Fact]
    public async Task A_cookie_for_another_domain_does_not_stop_long_polling()
    {
        using var server = new FakeBayeuxServer { CookieForAnotherDomain = CookieForAnotherDomain };
        using var http = server.CreateClient();
        var (options, received) = ChannelOptions("/topic/cookie", BayeuxTransportType.LongPolling);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/cookie", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

#if NET
    private sealed class UpgradeCounter : DelegatingHandler
    {
        private int _upgrades;

        public int Upgrades => Volatile.Read(ref _upgrades);

        public UpgradeCounter() : base(new HttpClientHandler { UseCookies = false }) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Contains("Sec-WebSocket-Version"))
                Interlocked.Increment(ref _upgrades);

            return base.SendAsync(request, cancellationToken);
        }
    }

    // Long polls go through; an upgrade is answered like any other request, as a handler that
    // does not know about upgrades would.
    private sealed class NoUpgrades : DelegatingHandler
    {
        public NoUpgrades() : base(new HttpClientHandler { UseCookies = false }) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.Headers.Contains("Sec-WebSocket-Version")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task The_upgrade_goes_through_the_handler_of_the_HttpClient()
    {
        using var server = new FakeBayeuxServer();
        var counter = new UpgradeCounter();
        using var http = new HttpClient(counter) { BaseAddress = new Uri(server.Prefix) };
        var (options, received) = ChannelOptions("/topic/handler", BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/handler", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(1, counter.Upgrades);
        Assert.Equal(1, server.WebSocketUpgrades);
    }

    [Fact]
    public async Task HttpClient_Timeout_does_not_limit_an_open_socket()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient(TimeSpan.FromMilliseconds(500));
        var (options, received) = ChannelOptions("/topic/timeout", BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        server.Publish("/topic/timeout", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(1, server.WebSocketUpgrades);
    }

    [Fact]
    public async Task A_handler_that_cannot_upgrade_fails_with_a_WebSocketException()
    {
        using var server = new FakeBayeuxServer();
        using var http = new HttpClient(new NoUpgrades()) { BaseAddress = new Uri(server.Prefix) };
        var (options, _) = ChannelOptions("/topic/handler", BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Auto_falls_back_when_the_handler_cannot_upgrade()
    {
        using var server = new FakeBayeuxServer();
        using var http = new HttpClient(new NoUpgrades()) { BaseAddress = new Uri(server.Prefix), Timeout = Timeout.InfiniteTimeSpan };
        var (options, received) = AutoOptions("/topic/auto");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/auto", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(BayeuxTransportType.LongPolling, client.ActiveTransportType);
    }

    [Fact]
    public async Task A_cookie_from_the_provider_joins_those_of_the_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var cookies = new CookieContainer();
        cookies.Add(new Uri(server.Prefix), new Cookie("session", "abc"));
        var auth = new DelegateAuthProvider(request => request.Headers.Add("Cookie", "login=xyz"));
        var (options, received) = ChannelOptions("/topic/cookie", BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, auth, (_, _) => { }, cookies);
        await client.ConnectAsync();

        server.Publish("/topic/cookie", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        var sent = Assert.Single(server.CookieHeadersSnapshot);
        Assert.Contains("session=abc", sent);
        Assert.Contains("login=xyz", sent);
        Assert.Contains("; ", sent);
    }

    [Fact]
    public async Task A_cookie_for_another_domain_does_not_stop_the_upgrade()
    {
        using var server = new FakeBayeuxServer { CookieForAnotherDomain = CookieForAnotherDomain };
        using var http = server.CreateClient();
        var (options, received) = ChannelOptions("/topic/cookie", BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.Publish("/topic/cookie", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }
#endif
}
