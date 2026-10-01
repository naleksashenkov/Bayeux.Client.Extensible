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
/// Application data as typed objects: <see cref="PollerOptions.JsonSerializerOptions"/>,
/// <see cref="BayeuxEvent.GetData{T}"/> and <see cref="BayeuxHandler.Of{T}"/>.
/// </summary>
/// <remarks>Part of <see cref="CometDPollerTests"/> to share its helpers.</remarks>
public partial class CometDPollerTests
{
    // A class rather than a record: a positional record needs init accessors, which net472 lacks.
    public sealed class Order
    {
        public int OrderId { get; set; }

        public decimal Total { get; set; }
    }

    private static PollerOptions TypedOptions(
        string channel, ConcurrentQueue<Order> received, JsonSerializerOptions? serializerOptions = null) =>
        new(
            new Dictionary<string, BayeuxEventHandler>
            {
                [channel] = BayeuxHandler.Of<Order>((order, _, _) => { received.Enqueue(order); return Task.CompletedTask; })
            },
            "cometd",
            jsonSerializerOptions: serializerOptions);

    [Fact]
    public async Task Published_data_is_camel_case_by_default()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/unrelated");

        await using var poller = new CometDPoller(http, options, NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        await poller.PublishAsync("/orders/new", new Order { OrderId = 7, Total = 9.5m });

        // What a server written in JavaScript expects, not the declared PascalCase.
        var data = LastPublishRequest(server).GetProperty("data");

        Assert.Equal(7, data.GetProperty("orderId").GetInt32());
        Assert.False(data.TryGetProperty("OrderId", out _));
    }

    [Fact]
    public async Task A_typed_handler_receives_the_object()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var received = new ConcurrentQueue<Order>();

        await using var poller = new CometDPoller(http, TypedOptions("/orders/new", received), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        server.Publish("/orders/new", new { orderId = 3, total = 1.5 });

        Assert.True(await WaitForAsync(() => received.Count == 1));

        var order = Assert.Single(received);
        Assert.Equal(3, order.OrderId);
        Assert.Equal(1.5m, order.Total);
    }

    [Fact]
    public async Task Custom_serializer_options_apply_both_ways()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var received = new ConcurrentQueue<Order>();

        var snakeCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        await using var poller = new CometDPoller(
            http, TypedOptions("/orders/new", received, snakeCase), NoAuthProvider.Instance, (_, _) => { });
        await poller.ConnectAsync();

        // Written with the poller's options...
        await poller.PublishAsync("/orders/new", new Order { OrderId = 7, Total = 9.5m });
        Assert.Equal(7, LastPublishRequest(server).GetProperty("data").GetProperty("order_id").GetInt32());

        // ...and read with them: the server delivers the message back to its subscriber, and the
        // handler can only fill OrderId if the poller passed the same options to the event.
        Assert.True(await WaitForAsync(() => received.Count == 1));

        var order = Assert.Single(received);
        Assert.Equal(7, order.OrderId);
        Assert.Equal(9.5m, order.Total);
    }

    [Fact]
    public async Task Data_that_does_not_fit_is_reported_and_the_poller_carries_on()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var received = new ConcurrentQueue<Order>();
        var errors = new ConcurrentQueue<OnPollerErrorEventArgs>();

        await using var poller = new CometDPoller(http, TypedOptions("/orders/new", received), NoAuthProvider.Instance, (_, _) => { });
        poller.OnError += (_, e) => errors.Enqueue(e);
        await poller.ConnectAsync();

        server.Publish("/orders/new", new { orderId = "seven" });

        // A handler exception like any other: reported, swallowed, polling continues.
        Assert.True(await WaitForAsync(() => errors.Any(e => e.Source == ErrorSource.Handler)));
        Assert.IsAssignableFrom<JsonException>(Assert.Single(errors, e => e.Source == ErrorSource.Handler).Error);
        Assert.Empty(received);

        server.Publish("/orders/new", new { orderId = 8, total = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task An_event_built_in_a_test_reads_like_one_from_the_poller()
    {
        // The handler's own unit test: no poller, no options - the defaults the poller would use.
        var @event = new BayeuxEvent("/orders/new", Json("""{"orderId":1,"total":2.5}"""), ext: null);

        var direct = @event.GetData<Order>()!;
        Assert.Equal(1, direct.OrderId);
        Assert.Equal(2.5m, direct.Total);

        Order? handled = null;
        var handler = BayeuxHandler.Of<Order>((order, e, _) =>
        {
            handled = order;
            Assert.Equal("/orders/new", e.Channel);
            return Task.CompletedTask;
        });

        await handler(@event, CancellationToken.None);

        Assert.Equal(1, handled!.OrderId);
    }
}
