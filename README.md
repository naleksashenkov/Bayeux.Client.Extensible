# Bayeux.Client.Extensible

**The existing .NET Bayeux clients don't support authentication.** They implement the protocol — handshake, subscribe, long-poll — and then assume the server will simply talk to you. Real deployments rarely do. One wants HTTP Basic on every request, another a session cookie obtained from a separate login, another a bearer token that expires mid-session. Bolting any of that onto a client with no seam for it means forking it and hoping the handshake still works.

This library adds that seam. Authentication is a first-class concept: an `IAuthProvider` the poller consults when it builds each request, so credentials can be attached and replaced at runtime without the protocol layer knowing how they were obtained.

Everything else is a conforming Bayeux long-polling client, written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux).

## Status

⚠️ **0.1.0-alpha.** The library is exercised end-to-end both against a scripted Bayeux stub and against the [CometD reference server](https://github.com/cometd/cometd-nodejs-server): handshake, subscribe, wildcard delivery, message routing, `402` re-handshake, `reconnect: none`, server-initiated disconnect, cookie isolation between pollers, batched subscribe and unsubscribe, partial-batch rollback, reconnect, publishing between clients. It has **not** yet been run against a production deployment under sustained load.

Implemented today: `CometDPoller`, `IAuthProvider`, `HttpBasicAuthProvider`, `NoAuthProvider`, wildcard subscriptions, batched subscribe and unsubscribe, publishing, typed data, `ext` extensions with acknowledgements, opt-in reconnection with backoff and jitter, optional `ILogger`. Long-polling only — there is no WebSocket transport. Bearer-token, refreshing-token and cookie-session providers, and Salesforce durable replay, are sketched in `Samples/`, not shipped.

## Install

```bash
dotnet add package Bayeux.Client.Extensible
```

Targets **net10.0**, **netstandard2.0** and **net472** — so .NET 5 through 10, .NET Core 2.0+, and .NET Framework 4.6.1+ can all consume it.

## Quick start

```csharp
var channels = new Dictionary<string, BayeuxEventHandler>
{
    ["/topic/orders"] = (e, _) =>
    {
        Console.WriteLine(e.Data);
        return Task.CompletedTask;
    }
};

var http = new HttpClient(new HttpClientHandler { UseCookies = false })
{
    BaseAddress = new Uri("https://example.com/"),
    Timeout = Timeout.InfiniteTimeSpan
};

await using var poller = new CometDPoller(
    http,
    new PollerOptions(channels, "cometd"),
    new HttpBasicAuthProvider(new BasicAuthCredentials("service-account", password)),
    onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

await poller.ConnectAsync();   // returns once the session is up; polling continues in the background
```

`ConnectAsync` completes when the handshake and subscriptions have succeeded. After that the poller runs on its own and messages arrive on the handlers you registered.

## The HttpClient contract

This is the part to get right; everything else is forgiving.

**`UseCookies = false` is required.** The poller manages session cookies itself, per request, so that several pollers can share one client without their sessions colliding. Leave the handler's default of `true` and both layers manage cookies — on the same host they overwrite each other and independent sessions silently collapse into one.

**Pass a long-lived client.** The library never creates or disposes one. Share it with your application's other calls to the same system if the CometD session must live inside the same server session — that is the normal case for cookie-authenticated deployments.

**One client per identity.** Several pollers on one client is the intended shape. The same client serving two different users is not: their cookies live in one place.

**Timeout must exceed the server's hold time.** The server holds `/meta/connect` open for `advice.timeout` — 30 seconds by default in CometD, and `HttpClient.Timeout` defaults to 100, so the defaults work. If a deployment configures a longer hold, every poll fails on a healthy connection. The poller checks this after the handshake and throws with both numbers rather than letting you discover it as a recurring timeout. `Timeout.InfiniteTimeSpan` is the simplest answer when the client is dedicated to CometD.

**On .NET Framework, raise the connection limit.** `ServicePointManager.DefaultConnectionLimit` defaults to **2**, and every poller holds one connection open permanently. The third poller against the same host blocks forever with nothing in the logs.

```csharp
ServicePointManager.DefaultConnectionLimit = Math.Max(
    ServicePointManager.DefaultConnectionLimit, pollerCount + 2);
```

## Logging

An optional `ILogger<CometDPoller>` can be passed as the last constructor argument:

```csharp
await using var poller = new CometDPoller(http, options, auth, onDisconnected, logger: myLogger);
```

Omit it and nothing is logged. The library logs session lifecycle and re-handshakes at `Information`, protocol detail at `Debug`, and **never logs request or response bodies** — those carry session identifiers and live data. Errors are not logged at all: they arrive through `OnError` and `OnPollerDisconnected`, and logging them too would make consumers filter the same failure twice.

The dependency is `Microsoft.Extensions.Logging.Abstractions`, referenced at a deliberately low version so it never forces an upgrade on your side.

## Authentication

**HTTP Basic** — credentials can be replaced at any time, with no reconnect:

```csharp
var auth = new HttpBasicAuthProvider(new BasicAuthCredentials("svc", password));

// later, on password rotation — takes effect on the next request
auth.UpdateCredentials(new BasicAuthCredentials("svc", newPassword));
```

**None** — for servers that need no authentication, and for deployments where a login elsewhere established the session and the cookie is carried in the `CookieContainer` you pass to the constructor:

```csharp
var auth = NoAuthProvider.Instance;
```

**Custom** — one method, called before every request including the handshake:

```csharp
public sealed class ApiKeyAuthProvider : IAuthProvider
{
    private readonly string _key;

    public ApiKeyAuthProvider(string key) => _key = key;

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation("X-API-Key", _key);
        return Task.CompletedTask;
    }
}
```

A provider must support replacing its credentials **in place** — the poller resolves it once and holds that instance for its lifetime, so a provider that needs reconstruction cannot rotate credentials without tearing down the session. Any field written by callers and read by `ApplyAsync` must be `volatile`. Implement `IAuthProvider<TCredentials>` to expose a typed `UpdateCredentials` and an `OnCredentialsUpdated` event.

## Extensions (`ext`)

Every Bayeux message may carry an `ext` object — the protocol's own extension point. Acknowledgement, time sync, Salesforce's durable replay and authentication inside the message all work through it. An extension usually negotiates: it asks in the handshake, and acts only once the server agrees in the reply.

Implement `IBayeuxExtension` and pass it in the options. This one puts credentials where a CometD server with a custom `SecurityPolicy` reads them — the exact shape is whatever your server expects:

```csharp
public sealed class ExtAuthentication : IBayeuxExtension
{
    private readonly string _user;
    private readonly string _token;

    public ExtAuthentication(string user, string token) => (_user, _token) = (user, token);

    public Task OutgoingAsync(BaseLongPollingRequestModel requestModel, CancellationToken cancellationToken)
    {
        if (requestModel.Channel == "/meta/handshake")
        {
            requestModel.Ext ??= new Dictionary<string, object>();
            requestModel.Ext["authentication"] = new { user = _user, credentials = _token };
        }

        return Task.CompletedTask;
    }

    public void Incoming(BayeuxResponseMessageModel responseModel) { }
}

var options = new PollerOptions(channels, "cometd", extensions: [new ExtAuthentication("svc", token)]);
```

What the poller guarantees:

- **`OutgoingAsync` runs for every message, before it is serialised** — once per message, not per HTTP request: five subscriptions in one batch are five calls, and one `IAuthProvider.ApplyAsync`. It is asynchronous because an extension may have to obtain something first, such as a fresh token — pass the `cancellationToken` to whatever it awaits, so that a stop or a cancelled call is not held up by an extension's I/O. A cancellation it causes is not reported as a failure.
- **If it throws, the message is not sent.** The operation fails, and `OnError` reports it once, as `ErrorSource.Ext`.
- **`Incoming` runs for every message the server sends back, before the poller acts on it and before any handler.** An extension therefore sees an event even when the handler for it throws. If `Incoming` throws, the failure is reported and the message is still delivered.
- **Both are called concurrently** — from the polling loop and from your own subscribe calls. Keep extension state thread-safe.
- **No extensions, no `ext`.** Without one registered, the poller sends exactly what it would send if the field did not exist.

Meta channel names — `/meta/handshake`, `/meta/connect` and the rest — are fixed by the protocol, and an extension compares them as plain strings.

**Salesforce durable streaming** — resuming after a dropped session, or even after a restart of your application, from the last event received — is in `Samples/`: `SalesforceReplayExtension` uses both directions, and `SalesforceReplayExample` shows it with a bearer token and positions saved to a file. It is a sample rather than part of the package because it is vendor-specific; copy it into your project.

## Handlers

A handler receives the event and a cancellation token, and returns a task:

```csharp
["/topic/**"] = async (e, cancellationToken) =>
    await store.SaveAsync(e.Channel, e.Data, cancellationToken)
```

`e.Channel` is the concrete channel the event arrived on — for a pattern like `/topic/**`, which of the matching channels it was. `e.Data` is the payload, `e.Ext` any extension data. A handler with nothing to await returns `Task.CompletedTask`.

**Handlers are awaited.** The next long poll is not sent until they finish, so a slow handler delays every event behind it while the server holds them. A handler that runs longer than the server's session timeout — CometD's `maxInterval`, 10 seconds by default — lets the session expire, and whatever the server was holding is lost. Keep handlers short, and hand long work to a queue of your own.

**Honour the token.** It is cancelled when the poller stops, and `DisconnectAsync` waits for a running handler — one that ignores the token holds up shutdown for as long as it runs. Cancellation it causes is not reported as an error.

**Exceptions** go to `OnError` as `ErrorSource.Handler`. They do not stop the poller, and the other handlers for the same event still receive it.

**Calling the poller from a handler:** `SubscribeNewChannelsAsync` and `UnsubscribeChannelsAsync` may be awaited. `DisconnectAsync` may be awaited too, but from inside a handler it only signals — the loop is waiting for that very handler, so it stops as soon as the handler returns. `ConnectAsync` throws: restarting would mean waiting for that loop. Reconnect from `OnPollerDisconnected`, which runs after the loop has finished.

## Typed data

`e.Data` is raw JSON. To receive an object instead, wrap the handler:

```csharp
channels["/orders/new"] = BayeuxHandler.Of<Order>(async (order, e, ct) =>
    await orders.SaveAsync(order, ct));
```

`BayeuxHandler.Of<T>` reads the data for each event as it is delivered and passes the whole event along, for its channel or `ext`. To the poller it is an ordinary handler; nothing else changes. Inside any handler, `e.GetData<T>()` does the same on demand.

Data is read and written with `PollerOptions.JsonSerializerOptions` — `JsonSerializerDefaults.Web` by default: camelCase names, read without regard to case. The same options serialize what `PublishAsync` sends, so one setting governs both directions:

```csharp
new PollerOptions(channels, "cometd",
    jsonSerializerOptions: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
```

Data that does not fit the type throws `JsonException` inside the handler, which is reported through `OnError` as `ErrorSource.Handler` like any handler exception; the poller carries on. The protocol's own fields are unaffected by these options.

## Channels

Handlers are registered per channel, either up front in `PollerOptions` or at runtime. Runtime subscriptions take a whole set and send it as **one** Bayeux message, so subscribing to ten channels costs one round trip, not ten:

```csharp
await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
{
    ["/topic/alerts"] = async (e, cancellationToken) => await HandleAsync(e.Data, cancellationToken),
    ["/topic/audit"]  = async (e, cancellationToken) => await AuditAsync(e.Data, cancellationToken)
});
```

Subscriptions are replayed automatically after a re-handshake, so they survive a dropped session.

`poller.Channels` is a read-only view of what is currently subscribed. It belongs to that poller alone: the dictionary you pass to `PollerOptions` is only the starting set, copied when each poller is created, so several pollers can share one options object without sharing subscriptions. Channels are added or removed only through `SubscribeNewChannelsAsync` and `UnsubscribeChannelsAsync` — the methods that also tell the server. A bare dictionary entry would never be subscribed, so the type no longer allows one.

**Wildcards are supported.** `/foo/*` matches one further segment, `/foo/**` any depth below. Patterns that overlap each deliver the message, so a message on `/foo/bar` reaches handlers registered for both `/foo/**` and `/foo/bar` — that is CometD's own behaviour, not a quirk of this client.

Unsubscribe by the same text you subscribed with:

```csharp
await poller.UnsubscribeChannelsAsync(["/topic/alerts", "/topic/audit"]);
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
    await poller.SubscribeNewChannelsAsync(channels);
}
catch (BayeuxSubscriptionException ex)
{
    // ex.Succeeded — subscribed and delivering
    // ex.Failures  — rejected, with the server's reason, and already rolled back
}
```

Either way `poller.Channels` still matches what the server believes: rejected channels are rolled back, and a refused *unsubscribe* puts the handler back, because the server still considers that subscription live and will keep sending its messages. Retrying only the failed channels is safe.

The same exception comes out of `ConnectAsync` when the server rejects a configured channel during setup. Setup does not continue with a partial subscription set — a session missing a channel you asked for is not the session you requested.

## Publishing

```csharp
await poller.PublishAsync("/chat/room1", new { text = "hi", from = "service-a" });
```

The task completes when the server has accepted the message. A refusal throws `BayeuxPublishException` with the server's `Error`; an HTTP failure throws `BayeuxHttpException`. Both go to the caller only — not to `OnError` — and neither affects the session.

- **camelCase by default.** The payload is serialized with `PollerOptions.JsonSerializerOptions` — `JsonSerializerDefaults.Web` unless you pass your own — so a property `OrderId` goes out as `orderId`, which is what servers written in JavaScript expect. A `JsonElement` is sent as it is. See [Typed data](#typed-data).
- **Never retried**, even with `ReconnectOptions`. A publish is not idempotent — repeating one after a timeout can deliver it twice — so whether to try again is your decision. Cancelling stops the wait, not a message already on its way.
- **You receive your own messages** if you are subscribed to the channel: CometD delivers to every subscriber, the publisher included. Sometimes inside the very reply to the publish, in which case your handler has run before `PublishAsync` returns.
- **Safe from a handler.** Publishing takes no lock, so a handler may await it.
- **`/service/` channels** address the server itself rather than other subscribers. Meta channels and wildcards are refused with `ArgumentException` before anything is sent.
- **Not every server accepts client publishes.** The Salesforce Streaming API does not; events are published there through its REST API.

## Lifecycle

| | |
|---|---|
| `ConnectAsync()` | Stops any current session, handshakes, subscribes, starts polling. Throws on failure. |
| `DisconnectAsync()` | Stops the loop, waits for it, releases the session. Safe when nothing is running. |
| `DisposeAsync()` | As above, plus releases resources. Shutdown has completed when it returns. |
| `Dispose()` | Signals and returns immediately — the loop may still send `/meta/disconnect` and raise the disconnect event afterwards. |

`ConnectAsync` always disconnects first, so calling it twice is safe. The lifecycle is **not thread-safe**; drive it from a single thread.

### Cancellation

Every call takes an optional `CancellationToken`. It stops **that call** from waiting — it never decides how long the session lives. The session belongs to the poller and ends only through `DisconnectAsync`, disposal, or the server.

| | What the token bounds | If it is cancelled |
|---|---|---|
| `ConnectAsync` | Setting up: handshake and subscriptions | Nothing is left half-open on the server; `OperationCanceledException` is thrown. Once connected, the token no longer matters — cancelling or disposing it later does not touch the session. |
| `SubscribeNewChannelsAsync` | The wait for the channel lock and for the server's answer | The whole batch is rolled back, as for any request whose outcome is unknown. Nothing from it is subscribed. |
| `UnsubscribeChannelsAsync` | The same | Every handler in the batch stays registered: the server may still consider those subscriptions live. |
| `DisconnectAsync` | Only the wait for the loop to finish | The stop itself cannot be cancelled. It is signalled before the wait begins, and the loop goes on stopping in the background. |

```csharp
// Give up on connecting after 10 seconds - the session, once up, is not affected.
using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    await poller.ConnectAsync(connectTimeout.Token);

// On shutdown, wait at most 5 seconds for a slow handler; the poller stops regardless.
using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try { await poller.DisconnectAsync(shutdown.Token); }
catch (OperationCanceledException) { /* still stopping in the background */ }
```

Handlers and extensions receive the **session's** token, not the caller's. It is cancelled when the poller stops, and a cancellation it causes is not reported as an error.

`ClientId` is non-empty only while a session is fully established — handshake **and** subscriptions. It empties and fills again on every re-handshake inside the polling loop, so an empty value on a running poller is normal recovery rather than a failure, and a runtime `SubscribeNewChannelsAsync` landing in that window is refused. Use it to correlate with server logs, not as a connection flag to poll: it is written by the poller's task and read by yours.

## Events

`OnPollerDisconnected` fires **once**, when the loop stops — and only if a session was established. A failed `ConnectAsync` throws instead.

```csharp
(_, e) =>
{
    e.Reason;            // TokenCancellation | ServerRequirement | Failed
    e.Error;             // what stopped the loop, if anything
    e.DisconnectError;   // why the goodbye failed, if it did
    e.IsSuccess;         // stopped cleanly and said goodbye cleanly
}
```

A failed `/meta/disconnect` is informational: the session ends regardless and the server reclaims it on timeout. `402::Unknown Client ID` there usually just means it had already expired.

`OnError` fires for each error observed, with the operation that produced it and whether the poller is about to stop:

```csharp
poller.OnError += (_, e) =>
{
    log.Warn($"{e.Source}: {e.Error.Message} (fatal: {e.IsFatal})");
};
```

Non-fatal sources include `Handler` — an exception from your own message handler, swallowed so a bug there cannot stop the poller — and `Connect` carrying CometD's `multiple-clients` advice, which means several pollers are sharing cookie state and all but one have been demoted to interval polling.

## Reconnecting

**Nothing is retried unless you ask.** By default any error ends the session: one dropped connection stops the poller, and the library never hides a failure by quietly recovering from it. (The one exception is the protocol's own: when the server invalidates a session with `402` or `advice.reconnect: "handshake"`, the poller handshakes again and resubscribes, because the server asked it to.)

To have the poller re-establish a session after an error, pass `ReconnectOptions`:

```csharp
var options = new PollerOptions(channels, "cometd", reconnectOptions: new ReconnectOptions());
```

After a failure the poller waits and tries again. **A transport failure keeps the session**: no answer, a timeout or an HTTP error says nothing about the session, which the server holds until its `maxInterval` with every event published meanwhile queued in it — so the poller retries `/meta/connect` with the same `ClientId` and loses nothing that was still queued. If the session did expire, the server answers `402` and the poller handshakes again. **A refusal ends the session**: after a refused handshake or connect the poller handshakes again and resubscribes every channel. Attempt *n* waits a random time between zero and `min(MaxDelay, InitialDelay × Multiplier^(n−1))` — one second doubling up to thirty by default. The bound grows so that a server that is down is not hammered; the randomness ("full jitter") keeps a thousand clients dropped by one restart from all coming back in the same millisecond. The count starts again once a `/meta/connect` succeeds.

**Only errors that can clear by themselves are retried.** `ReconnectOptions.IsRetriable`, the default, retries a lost connection, a timeout, HTTP 5xx, 408 and 429, and a refused handshake or connect whose advice invites another. It does **not** retry 401, 403, or a handshake or connect refused with `advice.reconnect: "none"`: repeating a rejected login every few seconds is how a service account gets locked. Failures are thrown as typed exceptions, so your own predicate can tell them apart:

| Exception | When | Carries |
|---|---|---|
| `BayeuxHttpException` | a status other than success | `StatusCode`, on every target framework |
| `BayeuxHandshakeException` | `/meta/handshake` answered `successful: false` | the server's `Error`, `Reconnect` advice and `Ext` |
| `BayeuxConnectException` | `/meta/connect` answered `successful: false` with advice `none` | the same three |

**A refusal is not a clean stop.** A server that ends a session successfully — a successful reply advising `none`, or a server-sent `/meta/disconnect` — stops the poller with `DisconnectReason.ServerRequirement` and no error. A *refused* connect stops it with `Failed` and a `BayeuxConnectException`, so the reason is never lost, and a policy may retry it.

That is how Salesforce reports a revoked access token: not HTTP 401, but `401::Authentication invalid` on `/meta/connect`, or `403::Handshake denied` with the real cause under `ext.sfdc.failureReason`. Retrying is worth it only because a fresh token is fetched before every attempt:

```csharp
new ReconnectOptions(
    maxDelay: TimeSpan.FromMinutes(1),
    maxAttempts: null,                                   // keep trying until stopped (the default)
    shouldRetry: e => e is BayeuxConnectException { Error: "401::Authentication invalid" }
                   || ReconnectOptions.IsRetriable(e),
    beforeAttemptAsync: async ct => auth.UpdateCredentials(await GetAccessTokenAsync(ct)));
```

`Samples/SalesforceReplayExample.cs` also reads `ext.sfdc.failureReason`, to catch a token found invalid at the handshake.

What to expect while it works:

- **`ConnectAsync` is never retried.** A wrong address or password throws at once rather than becoming an endless series of attempts.
- **After a refusal, there is no session between attempts.** `ClientId` is empty, and `SubscribeNewChannelsAsync` / `UnsubscribeChannelsAsync` throw `InvalidOperationException` at once. Channels already subscribed are resubscribed automatically. After a transport failure the session is kept, `ClientId` stays, and subscribing meanwhile works if the server is reachable.
- **`OnError` reports each failure with `IsFatal == false`**, because the poller carries on. `OnPollerDisconnected` fires only when it gives up — `Reason == Failed`, with the last error — or when you stop it.
- **`DisconnectAsync` interrupts a pending delay**, and nothing is attempted afterwards. Nothing is sent for a session already lost to a refusal; a session kept after a transport failure is closed as usual — which, if the server is still unreachable, waits up to `DisconnectTimeout`.
- **`BeforeAttemptAsync` runs before every attempt**, a retried connect as much as a new handshake: credentials can expire either way.

### Reconnecting by hand

Without `ReconnectOptions` the decision stays yours. The disconnect handler runs after the loop has finished, so it may reconnect directly:

```csharp
onPollerDisconnected: async (_, e) =>
{
    if (e.Reason == DisconnectReason.ServerRequirement)
        return;                                   // the server asked us to stop

    await Task.Delay(TimeSpan.FromSeconds(5));    // pace it yourself
    await poller.ConnectAsync();
}
```

Without a delay this retries as fast as the server can refuse. Exceptions thrown by the handler are swallowed.

## Acknowledgements

By default delivery is at-most-once: if the reply carrying events is lost — the connection drops after the server sent it — those events are gone. The acknowledge extension makes the server keep events until the client confirms them:

```csharp
new PollerOptions(channels, "cometd",
    extensions: [new BayeuxAckExtension()],
    reconnectOptions: new ReconnectOptions());
```

The server numbers each reply that carries events; the next `/meta/connect` tells it the last number received, and until then the events stay queued. When a reply is lost, the poller — with `ReconnectOptions` — retries the connect in the same session, still confirming the earlier batch, and the server sends the missing events again. Without `ReconnectOptions` the first failure stops the poller and the extension has nothing to recover.

- **Acknowledged means handled.** The confirmation goes out with the next connect, which the poller sends only after every handler for the previous events has returned.
- **At-least-once within a session.** An event can arrive twice — its reply arrived, but the confirmation did not — so handlers should tolerate duplicates. A session that ends, by expiry or a new handshake, still loses its queue.
- **The server must support it.** `IsAckSupported` says whether it agreed in the last handshake; a server that did not sees nothing different. With acknowledgements on, CometD delivers events only in `/meta/connect` replies.

## Protocol provenance

Written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux). Message field names, the `/meta/` channel semantics and the advice-driven reconnect behaviour all follow the specification text. No code is derived from any other Bayeux implementation.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Contributions require a Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md) and [DCO](DCO).
