# Bayeux.Client.Extensible

**The existing .NET Bayeux clients don't support authentication.** They implement the protocol — handshake, subscribe, long-poll — and then assume the server will simply talk to you. Real deployments rarely do. One wants HTTP Basic on every request, another a session cookie obtained from a separate login, another a bearer token that expires mid-session. Bolting any of that onto a client with no seam for it means forking it and hoping the handshake still works.

This library adds that seam. Authentication is a first-class concept: an `IAuthProvider` the poller consults when it builds each request, so credentials can be attached and replaced at runtime without the protocol layer knowing how they were obtained.

Everything else is a conforming Bayeux long-polling client, written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux).

## Status

⚠️ **0.1.0-alpha.** The library is exercised end-to-end both against a scripted Bayeux stub and against the [CometD reference server](https://github.com/cometd/cometd-nodejs-server): handshake, subscribe, wildcard delivery, message routing, `402` re-handshake, `reconnect: none`, server-initiated disconnect, cookie isolation between pollers, batched subscribe and unsubscribe, partial-batch rollback, reconnect. It has **not** yet been run against a production deployment under sustained load.

Implemented today: `CometDPoller`, `IAuthProvider`, `HttpBasicAuthProvider`, `NoAuthProvider`, wildcard subscriptions, batched subscribe and unsubscribe, `ext` extensions, optional `ILogger`. Long-polling only — there is no WebSocket transport. Bearer-token, refreshing-token and cookie-session providers, and Salesforce durable replay, are sketched in `Samples/`, not shipped.

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
    new CometdPollerOptions(channels, "cometd"),
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

Implement `IBayeuxExt` and pass it in the options. This one puts credentials where a CometD server with a custom `SecurityPolicy` reads them — the exact shape is whatever your server expects:

```csharp
public sealed class ExtAuthentication : IBayeuxExt
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

var options = new CometdPollerOptions(channels, "cometd", extensions: [new ExtAuthentication("svc", token)]);
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

## Channels

Handlers are registered per channel, either up front in `CometdPollerOptions` or at runtime. Runtime subscriptions take a whole set and send it as **one** Bayeux message, so subscribing to ten channels costs one round trip, not ten:

```csharp
await poller.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler>
{
    ["/topic/alerts"] = async (e, cancellationToken) => await HandleAsync(e.Data, cancellationToken),
    ["/topic/audit"]  = async (e, cancellationToken) => await AuditAsync(e.Data, cancellationToken)
});
```

Subscriptions are replayed automatically after a re-handshake, so they survive a dropped session.

`Options.Channels` is a read-only view of what is currently subscribed. The dictionary you pass to `CometdPollerOptions` is copied, and channels are added or removed only through `SubscribeNewChannelsAsync` and `UnsubscribeChannelsAsync` — the methods that also tell the server. A bare dictionary entry would never be subscribed, so the type no longer allows one.

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

Either way `Options.Channels` still matches what the server believes: rejected channels are rolled back, and a refused *unsubscribe* puts the handler back, because the server still considers that subscription live and will keep sending its messages. Retrying only the failed channels is safe.

The same exception comes out of `ConnectAsync` when the server rejects a configured channel during setup. Setup does not continue with a partial subscription set — a session missing a channel you asked for is not the session you requested.

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

## There is no automatic retry

Any error ends the session. One dropped connection stops the poller, and reconnecting is your decision — the library never retries behind your back, and never hides a failure by quietly recovering from it.

The disconnect handler runs after the loop has finished, so it may reconnect directly:

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

## Protocol provenance

Written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux). Message field names, the `/meta/` channel semantics and the advice-driven reconnect behaviour all follow the specification text. No code is derived from any other Bayeux implementation.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Contributions require a Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md) and [DCO](DCO).
