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
/// <see cref="BayeuxAckExtension"/> on its own, and together with the poller retrying a connect
/// in the same session.
/// </summary>
/// <remarks>Part of <see cref="CometDPollerTests"/> to share its helpers.</remarks>
public partial class CometDPollerTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static BayeuxResponseMessageModel Reply(string channel, string ackJson, bool successful = true) =>
        new()
        {
            Channel = channel,
            IsSuccessful = successful,
            Ext = new Dictionary<string, JsonElement> { ["ack"] = Json(ackJson) }
        };

    // What the extension adds to a connect, or null when it adds nothing.
    private static async Task<object?> ConfirmedOnConnect(BayeuxAckExtension extension, ConnectRequestModel? connect = null)
    {
        connect ??= new ConnectRequestModel("cid", "1");
        await extension.OutgoingAsync(connect, CancellationToken.None);

        return connect.Ext is { } ext && ext.TryGetValue("ack", out var ack) ? ack : null;
    }

    // ---- the extension on its own --------------------------------------------------------------

    [Fact]
    public async Task The_ack_extension_asks_in_the_handshake_and_waits_for_agreement()
    {
        var extension = new BayeuxAckExtension();
        var handshake = new HandshakeRequestModel("1");

        await extension.OutgoingAsync(handshake, CancellationToken.None);

        Assert.Equal(true, handshake.Ext!["ack"]);

        // Not before the server agrees: a connect to a server that never heard of the extension
        // is left as it is.
        Assert.False(extension.IsAckSupported);
        Assert.Null(await ConfirmedOnConnect(extension));
    }

    [Fact]
    public async Task The_ack_extension_confirms_the_last_batch_of_a_successful_connect()
    {
        var extension = new BayeuxAckExtension();
        await extension.OutgoingAsync(new HandshakeRequestModel("1"), CancellationToken.None);
        extension.Incoming(Reply("/meta/handshake", "true"));

        Assert.True(extension.IsAckSupported);
        Assert.Equal(0L, await ConfirmedOnConnect(extension));          // nothing received yet

        extension.Incoming(Reply("/meta/connect", "5"));
        Assert.Equal(5L, await ConfirmedOnConnect(extension));

        // A failed reply numbers nothing: it carried no events to confirm.
        extension.Incoming(Reply("/meta/connect", "9", successful: false));
        Assert.Equal(5L, await ConfirmedOnConnect(extension));
    }

    [Fact]
    public async Task A_new_handshake_starts_the_batches_again()
    {
        var extension = new BayeuxAckExtension();
        await extension.OutgoingAsync(new HandshakeRequestModel("1"), CancellationToken.None);
        extension.Incoming(Reply("/meta/handshake", "true"));
        extension.Incoming(Reply("/meta/connect", "5"));

        // Batches belong to a session; confirming batch 5 of the old one to the new one would
        // confirm events it never sent.
        await extension.OutgoingAsync(new HandshakeRequestModel("2"), CancellationToken.None);
        Assert.False(extension.IsAckSupported);

        extension.Incoming(Reply("/meta/handshake", "true"));
        Assert.Equal(0L, await ConfirmedOnConnect(extension));
    }

    [Fact]
    public async Task The_ack_extension_understands_the_object_form_of_agreement()
    {
        // Newer servers answer { enabled, batch } instead of true.
        var agreed = new BayeuxAckExtension();
        await agreed.OutgoingAsync(new HandshakeRequestModel("1"), CancellationToken.None);
        agreed.Incoming(Reply("/meta/handshake", """{"enabled":true,"batch":7}"""));

        Assert.True(agreed.IsAckSupported);
        Assert.Equal(7L, await ConfirmedOnConnect(agreed));

        var declined = new BayeuxAckExtension();
        await declined.OutgoingAsync(new HandshakeRequestModel("1"), CancellationToken.None);
        declined.Incoming(Reply("/meta/handshake", """{"enabled":false}"""));

        Assert.False(declined.IsAckSupported);
        Assert.Null(await ConfirmedOnConnect(declined));
    }

    [Fact]
    public async Task The_ack_extension_leaves_other_extensions_keys_in_place()
    {
        var extension = new BayeuxAckExtension();
        await extension.OutgoingAsync(new HandshakeRequestModel("1"), CancellationToken.None);
        extension.Incoming(Reply("/meta/handshake", "true"));

        var connect = new ConnectRequestModel("cid", "2")
        {
            Ext = new Dictionary<string, object> { ["replay"] = "kept" }
        };

        Assert.Equal(0L, await ConfirmedOnConnect(extension, connect));
        Assert.Equal("kept", connect.Ext!["replay"]);
    }

    // ---- with the poller ------------------------------------------------------------------------

    [Fact]
    public async Task With_acknowledgements_an_event_whose_reply_was_lost_is_delivered_again()
    {
        using var server = new FakeBayeuxServer { AckEnabled = true };
        using var http = server.CreateClient();

        var received = new ConcurrentQueue<JsonElement>();
        var ack = new BayeuxAckExtension();

        var options = new PollerOptions(
            new Dictionary<string, BayeuxEventHandler> { ["/topic/a"] = On(data => received.Enqueue(data)) },
            "cometd",
            extensions: [ack],
            reconnectOptions: QuickReconnect());

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        Assert.True(ack.IsAckSupported);

        // The reply carrying the event fails on its way: taken off the queue, never delivered.
        server.LoseNextReplyWithEvents = true;
        server.Publish("/topic/a", new { n = 1 });

        // A transport failure, so the poller retries the connect in the same session - still
        // confirming the batch before the lost one - and the server sends the event again.
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(1, server.HandshakeCount);

        // Once confirmed, it is gone from the server: no second delivery.
        Assert.True(await WaitForAsync(() => server.AcksReceivedSnapshot.Contains(1)));
        await Task.Delay(500);

        Assert.Equal(1, Assert.Single(received).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Without_acknowledgements_an_event_whose_reply_was_lost_is_gone()
    {
        // The same failure without the extension: what acknowledgements are for.
        using var server = new FakeBayeuxServer { AckEnabled = true };
        using var http = server.CreateClient();
        var (options, received) = ReconnectingOptions("/topic/a", QuickReconnect());

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.LoseNextReplyWithEvents = true;
        server.Publish("/topic/a", new { n = 1 });

        Assert.True(await WaitForAsync(() => !server.LoseNextReplyWithEvents));
        await Task.Delay(1500);
        Assert.Empty(received);

        // The session itself carries on.
        server.Publish("/topic/a", new { n = 2 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
        Assert.Equal(2, Assert.Single(received).GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task A_server_without_acknowledgements_gets_plain_connects()
    {
        using var server = new FakeBayeuxServer();          // AckEnabled is off
        using var http = server.CreateClient();

        var ack = new BayeuxAckExtension();
        await using var poller = new CometDPoller(
            http, OptionsWith(ack, "/topic/a"), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        Assert.False(ack.IsAckSupported);
        Assert.True(await WaitForAsync(() => server.RequestBodiesSnapshot.Count(b => b.StartsWith("/cometd/connect ", StringComparison.Ordinal)) >= 1));

        Assert.All(
            server.RequestBodiesSnapshot.Where(b => b.StartsWith("/cometd/connect ", StringComparison.Ordinal)),
            body => Assert.DoesNotContain("\"ack\"", body));
    }
}
