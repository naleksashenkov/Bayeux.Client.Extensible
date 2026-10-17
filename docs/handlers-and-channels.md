# Handlers, channels and publishing

Receiving events, typed data, subscribing at runtime, and publishing.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

## Handlers

A handler receives the event and a cancellation token, and returns a task:

```csharp
["/topic/**"] = async (e, cancellationToken) =>
    await store.SaveAsync(e.Channel, e.Data, cancellationToken)
```

`e.Channel` is the concrete channel the event arrived on — for a pattern like `/topic/**`, which of the matching channels it was. `e.Data` is the payload, `e.Ext` any extension data. A handler with nothing to await returns `Task.CompletedTask`.

**Handlers are awaited.** The next long poll is not sent until they finish, so a slow handler delays every event behind it while the server holds them. A handler that runs longer than the server's session timeout — CometD's `maxInterval`, 10 seconds by default — lets the session expire, and whatever the server was holding is lost. Keep handlers short, and hand long work to a queue of your own. Over a [WebSocket](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/transports.md#transports) the connect does not wait for handlers, so the session is not at risk; events still run one at a time, and a slow handler still delays the ones behind it.

**Honour the token.** It is cancelled when the client stops, and `DisconnectAsync` waits for a running handler — one that ignores the token holds up shutdown for as long as it runs. Cancellation it causes is not reported as an error.

**Exceptions** go to `OnError` as `ErrorSource.Handler`. They do not stop the client, and the other handlers for the same event still receive it.

**Calling the client from a handler:** `SubscribeNewChannelsAsync` and `UnsubscribeChannelsAsync` may be awaited. `DisconnectAsync` may be awaited too, but from inside a handler it only signals — the loop is waiting for that very handler, so it stops as soon as the handler returns. `ConnectAsync` throws: restarting would mean waiting for that loop. Reconnect from `OnDisconnected`, which runs after the loop has finished.

## Typed data

`e.Data` is raw JSON. To receive an object instead, wrap the handler:

```csharp
channels["/orders/new"] = BayeuxHandler.Of<Order>(async (order, e, ct) =>
    await orders.SaveAsync(order, ct));
```

`BayeuxHandler.Of<T>` reads the data for each event as it is delivered and passes the whole event along, for its channel or `ext`. To the client it is an ordinary handler; nothing else changes. Inside any handler, `e.GetData<T>()` does the same on demand.

Data is read and written with `BayeuxClientOptions.JsonSerializerOptions` — `JsonSerializerDefaults.Web` by default: camelCase names, read without regard to case. The same options serialize what `PublishAsync` sends, so one setting governs both directions:

```csharp
new BayeuxClientOptions(channels, "cometd",
    jsonSerializerOptions: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
```

Data that does not fit the type throws `JsonException` inside the handler, which is reported through `OnError` as `ErrorSource.Handler` like any handler exception; the client carries on. The protocol's own fields are unaffected by these options.

## Channels

Handlers are registered per channel, either up front in `BayeuxClientOptions` or at runtime. Runtime subscriptions take a whole set and send it as **one** Bayeux message, so subscribing to ten channels costs one round trip, not ten:

```csharp
await client.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
{
    ["/topic/alerts"] = async (e, cancellationToken) => await HandleAsync(e.Data, cancellationToken),
    ["/topic/audit"]  = async (e, cancellationToken) => await AuditAsync(e.Data, cancellationToken)
});
```

Subscriptions are replayed automatically after a re-handshake, so they survive a dropped session.

`client.Channels` is a read-only view of what is currently subscribed. It belongs to that client alone: the dictionary you pass to `BayeuxClientOptions` is only the starting set, copied when each client is created, so several clients can share one options object without sharing subscriptions. Channels are added or removed only through `SubscribeNewChannelsAsync` and `UnsubscribeChannelsAsync` — the methods that also tell the server. A bare dictionary entry would never be subscribed, so the type no longer allows one.

**Wildcards are supported.** `/foo/*` matches one further segment, `/foo/**` any depth below. Patterns that overlap each deliver the message, so a message on `/foo/bar` reaches handlers registered for both `/foo/**` and `/foo/bar` — that is CometD's own behaviour, not a quirk of this client.

Unsubscribe by the same text you subscribed with:

```csharp
await client.UnsubscribeChannelsAsync(["/topic/alerts", "/topic/audit"]);
```

Removing `/topic/**` leaves a separate `/topic/orders` in place.

### When a batch only partly succeeds

A batch has two distinct failure modes, and the exception type tells them apart:

| What happened | What you get | What is subscribed |
|---|---|---|
| The server answered and rejected some channels | `BayeuxSubscriptionException` | the ones in `Succeeded` |
| The request never got an answer | the transport exception, unchanged | nothing from the batch |

```csharp
try
{
    await client.SubscribeNewChannelsAsync(channels);
}
catch (BayeuxSubscriptionException ex)
{
    // ex.Succeeded — subscribed and delivering
    // ex.Failures  — rejected, with the server's reason, and already rolled back
}
```

Either way `client.Channels` still matches what the server believes: rejected channels are rolled back, and a refused *unsubscribe* puts the handler back, because the server still considers that subscription live and will keep sending its messages. Retrying only the failed channels is safe.

The same exception comes out of `ConnectAsync` when the server rejects a configured channel during setup. Setup does not continue with a partial subscription set — a session missing a channel you asked for is not the session you requested.

## Publishing

```csharp
await client.PublishAsync("/chat/room1", new { text = "hi", from = "service-a" });
```

The task completes when the server has accepted the message. A refusal throws `BayeuxPublishException` with the server's `Error`; an HTTP failure throws `BayeuxHttpException`. Both go to the caller only — not to `OnError` — and neither affects the session.

- **camelCase by default.** The payload is serialized with `BayeuxClientOptions.JsonSerializerOptions` — `JsonSerializerDefaults.Web` unless you pass your own — so a property `OrderId` goes out as `orderId`, which is what servers written in JavaScript expect. A `JsonElement` is sent as it is. See [Typed data](#typed-data).
- **Never retried**, even with `ReconnectOptions`. A publish is not idempotent — repeating one after a timeout can deliver it twice — so whether to try again is your decision. Cancelling stops the wait, not a message already on its way.
- **You receive your own messages** if you are subscribed to the channel: CometD delivers to every subscriber, the publisher included. Sometimes inside the very reply to the publish, in which case your handler has run before `PublishAsync` returns.
- **Safe from a handler.** Publishing takes no lock, so a handler may await it.
- **`/service/` channels** address the server itself rather than other subscribers. Meta channels and wildcards are refused with `ArgumentException` before anything is sent.
- **Not every server accepts client publishes.** The Salesforce Streaming API does not; events are published there through its REST API.
