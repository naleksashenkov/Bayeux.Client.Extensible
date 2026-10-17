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
/// <see cref="BayeuxTimesyncExtension"/>: the server's clock found from the replies, the hold of a
/// long poll kept out of the network delay, and nothing broken by a server that does not answer.
/// </summary>
/// <remarks>Part of <see cref="BayeuxClientTests"/> to share its helpers.</remarks>
public partial class BayeuxClientTests
{
    private static BayeuxClientOptions TimesyncOptions(BayeuxTransportType transport, params IBayeuxExtension[] extensions) =>
        new(new Dictionary<string, BayeuxEventHandler> { ["/topic/time"] = Ignore }, "cometd",
            extensions: extensions, transportType: transport);

    // A reply as the server would send it: our clock echoed, its own on receipt, the time it held it.
    private static BayeuxResponseMessageModel TimesyncReply(string channel, long tc, long ts, long p) => new()
    {
        Channel = channel,
        Ext = new Dictionary<string, JsonElement> { ["timesync"] = Json($$"""{"tc":{{tc}},"ts":{{ts}},"p":{{p}},"a":0}""") }
    };

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Theory]
    [InlineData(BayeuxTransportType.LongPolling)]
    [InlineData(BayeuxTransportType.WebSocket)]
    public async Task The_servers_clock_is_found_and_the_hold_is_not_taken_for_network_delay(BayeuxTransportType transport)
    {
        // The server is 5 s ahead and holds each connect for about a second. Were the hold counted
        // as network delay, the lag would be half a second and the offset half a second short.
        using var server = new FakeBayeuxServer
        {
            TimesyncEnabled = true,
            ServerClockOffset = TimeSpan.FromSeconds(5),
            WebSocketConnectHold = TimeSpan.FromSeconds(1)
        };
        using var http = server.CreateClient();
        var timesync = new BayeuxTimesyncExtension();

        await using var client = new BayeuxClient(http, TimesyncOptions(transport, timesync), NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        // A few held connects answered.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.True(timesync.IsTimesyncSupported);
        Assert.InRange(timesync.Offset.TotalMilliseconds, 4800, 5200);
        Assert.InRange(timesync.Lag.TotalMilliseconds, 0, 200);
        Assert.InRange((timesync.ServerTime - DateTimeOffset.UtcNow).TotalMilliseconds, 4800, 5200);
    }

    [Fact]
    public async Task Without_the_servers_side_nothing_is_estimated_and_nothing_breaks()
    {
        using var server = new FakeBayeuxServer();   // timesync off
        using var http = server.CreateClient();
        var timesync = new BayeuxTimesyncExtension();
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, TimesyncOptions(BayeuxTransportType.LongPolling, timesync), NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // The request went out regardless: a server that does not know it ignores it.
        Assert.Contains("\"timesync\"", server.BodyFor("handshake"));

        Assert.False(timesync.IsTimesyncSupported);
        Assert.Equal(TimeSpan.Zero, timesync.Offset);
        Assert.InRange((timesync.ServerTime - DateTimeOffset.UtcNow).TotalMilliseconds, -100, 100);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("""{"tc":"soon","ts":1,"p":0}""")]
    [InlineData("""{"tc":1}""")]
    public async Task A_reply_it_cannot_read_is_skipped_not_reported(string reply)
    {
        using var server = new FakeBayeuxServer { TimesyncEnabled = true, TimesyncReplyOverride = reply };
        using var http = server.CreateClient();
        var timesync = new BayeuxTimesyncExtension();
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, TimesyncOptions(BayeuxTransportType.LongPolling, timesync), NoAuthProvider.Instance, (_, _) => { });
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        Assert.False(timesync.IsTimesyncSupported);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Timesync_and_acknowledgements_share_ext_in_either_order()
    {
        foreach (var order in new[] { new IBayeuxExtension[] { new BayeuxAckExtension(), new BayeuxTimesyncExtension() },
                                      new IBayeuxExtension[] { new BayeuxTimesyncExtension(), new BayeuxAckExtension() } })
        {
            using var server = new FakeBayeuxServer();
            using var http = server.CreateClient();

            await using var client = new BayeuxClient(http, TimesyncOptions(BayeuxTransportType.LongPolling, order), NoAuthProvider.Instance, (_, _) => { });
            await client.ConnectAsync();

            // Each adds its key; neither replaces the other's.
            var handshake = server.BodyFor("handshake");
            Assert.Contains("\"ack\":true", handshake);
            Assert.Contains("\"timesync\"", handshake);
        }
    }

    [Fact]
    public async Task Only_handshake_and_connect_carry_the_clock()
    {
        var timesync = new BayeuxTimesyncExtension();

        var handshake = new HandshakeRequestModel("1", "long-polling");
        var connect = new ConnectRequestModel("c", "2", "long-polling");
        var subscribe = new SubscribeRequestModel("c", "/topic/x", "3");

        foreach (var message in new BayeuxRequestModel[] { handshake, connect, subscribe })
            await timesync.OutgoingAsync(message, CancellationToken.None);

        Assert.True(handshake.Ext!.ContainsKey("timesync"));
        Assert.True(connect.Ext!.ContainsKey("timesync"));
        Assert.Null(subscribe.Ext);
    }

    [Fact]
    public void The_estimate_averages_the_latest_samples_only()
    {
        var timesync = new BayeuxTimesyncExtension(maxSamples: 2);

        // Sent 100 ms ago, not held: a lag of 50 ms, and the server's ts chosen for a given offset.
        void Sample(long offset)
        {
            var tc = NowMs - 100;
            timesync.Incoming(TimesyncReply("/meta/connect", tc, tc + 50 + offset, 0));
        }

        Sample(1000);
        Sample(3000);
        Assert.InRange(timesync.Offset.TotalMilliseconds, 1980, 2020);

        // The oldest drops out: (3000 + 5000) / 2.
        Sample(5000);
        Assert.InRange(timesync.Offset.TotalMilliseconds, 3980, 4020);
        Assert.InRange(timesync.Lag.TotalMilliseconds, 45, 70);
    }

    [Fact]
    public void A_sample_with_a_negative_lag_is_dropped()
    {
        // This machine's clock stepped back between send and receive: "sent" lies in the future.
        var timesync = new BayeuxTimesyncExtension();
        var tc = NowMs + 10_000;

        timesync.Incoming(TimesyncReply("/meta/connect", tc, tc, 0));

        Assert.False(timesync.IsTimesyncSupported);
        Assert.Equal(TimeSpan.Zero, timesync.Offset);
    }

    [Fact]
    public void Timesync_on_other_channels_is_ignored()
    {
        var timesync = new BayeuxTimesyncExtension();
        var tc = NowMs - 100;

        timesync.Incoming(TimesyncReply("/meta/subscribe", tc, tc + 5000, 0));
        timesync.Incoming(TimesyncReply("/topic/data", tc, tc + 5000, 0));

        Assert.False(timesync.IsTimesyncSupported);
    }

    [Fact]
    public void At_least_one_sample_is_required()
    {
        Assert.Throws<ArgumentOutOfRangeException>("maxSamples", () => new BayeuxTimesyncExtension(0));
    }
}
