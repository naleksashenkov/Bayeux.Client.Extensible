# Bayeux.Client.Extensible

![Bayeux.Client.Extensible — a swallow carrying a key toward a keyhole star](https://raw.githubusercontent.com/naleksashenkov/Bayeux.Client.Extensible/main/assets/logo-128.png)

[![NuGet](https://img.shields.io/nuget/vpre/Bayeux.Client.Extensible?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Bayeux.Client.Extensible)
[![CI](https://github.com/naleksashenkov/Bayeux.Client.Extensible/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/naleksashenkov/Bayeux.Client.Extensible/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/naleksashenkov/Bayeux.Client.Extensible)](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/LICENSE)

A Bayeux / CometD client for .NET — over WebSocket and long-polling — with authentication built in.

**The existing .NET Bayeux clients don't support authentication.** They implement the protocol — handshake, subscribe, long-poll — and then assume the server will simply talk to you. Real deployments rarely do. One wants HTTP Basic on every request, another a session cookie obtained from a separate login, another a bearer token that expires mid-session. Bolting any of that onto a client with no seam for it means forking it and hoping the handshake still works.

This library adds that seam. Authentication is a first-class concept: an `IAuthProvider` the client consults when it builds each request, so credentials can be attached and replaced at runtime without the protocol layer knowing how they were obtained.

Everything else is a conforming Bayeux client — over HTTP long-polling or a WebSocket — written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux).

## Status

⚠️ **1.0.0-alpha.1 — the API may still change before 1.0.** The library is exercised end-to-end both against a scripted Bayeux stub and against the CometD reference servers — the [Java original](https://github.com/cometd/cometd) over long-polling and WebSocket, and its [Node.js port](https://github.com/cometd/cometd-nodejs-server) over long-polling: handshake, subscribe, wildcard delivery, message routing, `402` re-handshake, `reconnect: none`, server-initiated disconnect, cookie isolation between clients, batched subscribe and unsubscribe, partial-batch rollback, reconnect, publishing between clients, acknowledgements. It has **not** yet been run against a production deployment under sustained load.

Implemented today: `BayeuxClient`, `IAuthProvider`, `HttpBasicAuthProvider`, `BearerTokenAuthProvider`, `RefreshingTokenAuthProvider`, `ApiKeyAuthProvider`, `DelegateAuthProvider`, `NoAuthProvider`, wildcard subscriptions, batched subscribe and unsubscribe, publishing, typed data, connection state, `ext` extensions with acknowledgements and timesync, opt-in reconnection with backoff and jitter, optional `ILogger`, long-polling and WebSocket transports. Salesforce durable replay is sketched in `Samples/`, not shipped.

## Coming from CometD.NetCore2?

[CometD.NetCore2](https://github.com/kdcllc/CometD.NetCore) is the most used Bayeux client for .NET. What this library adds:

- **WebSocket**, and `Auto` — WebSocket where it gets through, long-polling where it does not. CometD.NetCore2 has long-polling only.
- **Authentication that can change** while the session runs — Basic, bearer, a refreshing OAuth token, API keys, your own — where CometD.NetCore2 fixes the headers when the transport is created.
- **`async` throughout**: `HttpClient` instead of `HttpWebRequest`, asynchronous handlers with a cancellation token, `ConnectAsync` instead of `Handshake` and `WaitFor`.
- **Reconnecting** with backoff and jitter that keeps the session across network failures, and subscriptions restored after every new handshake.
- **System.Text.Json**, with typed data.

What it does not have yet: a shipped Salesforce replay extension (there is a sample). The [migration guide](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/migrating-from-cometd-netcore2.md) maps one API onto the other, step by step.

## Install

```bash
dotnet add package Bayeux.Client.Extensible --prerelease
```

`--prerelease` while the package is in alpha: without it `dotnet add package` looks for a stable version, and there is none yet.

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

await using var client = new BayeuxClient(
    http,
    new BayeuxClientOptions(channels, "cometd"),
    new HttpBasicAuthProvider(new BasicAuthCredentials("service-account", password)),
    onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

await client.ConnectAsync();   // returns once the session is up; polling continues in the background
```

`ConnectAsync` completes when the handshake and subscriptions have succeeded. After that the client runs on its own and messages arrive on the handlers you registered.

## The HttpClient contract

This is the part to get right; everything else is forgiving.

**`UseCookies = false` is required.** The client manages session cookies itself, per request, so that several Bayeux clients can share one `HttpClient` without their sessions colliding. Leave the handler's default of `true` and both layers manage cookies — on the same host they overwrite each other and independent sessions silently collapse into one.

**Pass a long-lived `HttpClient`.** The library never creates or disposes one. Share it with your application's other calls to the same system if the Bayeux session must live inside the same server session — that is the normal case for cookie-authenticated deployments.

**One `HttpClient` per identity.** Several Bayeux clients on one `HttpClient` is the intended shape. The same `HttpClient` serving two different users is not: their cookies live in one place.

**Timeout must exceed the server's hold time.** The server holds `/meta/connect` open for `advice.timeout` — 30 seconds by default in CometD, and `HttpClient.Timeout` defaults to 100, so the defaults work. If a deployment configures a longer hold, every poll fails on a healthy connection. The client checks this after the handshake and throws with both numbers rather than letting you discover it as a recurring timeout. `Timeout.InfiniteTimeSpan` is the simplest answer when the `HttpClient` is dedicated to Bayeux.

**With a WebSocket, only `BaseAddress` matters.** The socket is the client's own; the timeout and the handler's settings do not apply to it. See [Transports](#transports).

**On .NET Framework, raise the connection limit.** `ServicePointManager.DefaultConnectionLimit` defaults to **2**, and every Bayeux client holds one connection open permanently. The third one against the same host blocks forever with nothing in the logs.

```csharp
ServicePointManager.DefaultConnectionLimit = Math.Max(
    ServicePointManager.DefaultConnectionLimit, clientCount + 2);
```

## Transports

Long-polling is the default, and works through every proxy. A WebSocket keeps one connection for the whole session, and the server pushes each event the moment it has it instead of holding it for the next `/meta/connect` reply:

```csharp
new BayeuxClientOptions(channels, "cometd", transportType: BayeuxTransportType.WebSocket);
```

- **Same address, same path.** `http` becomes `ws` and `https` becomes `wss`. The `HttpClient` supplies only its `BaseAddress`: the socket is opened by the client, so the handler's proxy and certificates and `HttpClient.Timeout` do not apply to it.
- **Credentials go out once per socket**, in the upgrade request. The `IAuthProvider` is called for it like for any request, and the headers it sets are carried over — nothing else is. New credentials from `UpdateCredentials` take effect when the next socket opens. Cookies work as with long-polling: the client's container is sent with the upgrade, and what the server sets on it is kept.
- **The server must offer it.** A server without WebSocket refuses the upgrade and `ConnectAsync` throws `WebSocketException`; one that accepts the socket but does not list `websocket` in the handshake fails with `ErrorSource.Configuration`. Use `Auto`, below, to fall back to long-polling instead.
- **Events run in order, apart from the connect.** A handler sees a channel's events in the order the server sent them, one after another, as with long-polling. But the next `/meta/connect` does not wait for them — except with [acknowledgements](#acknowledgements), where CometD sends events only in connect replies.
- **A dropped socket is replaced** by the next request; the session goes on over the new one.
- **The server may end the session on its own**, with a `/meta/disconnect` pushed over the socket. The client stops at once with `DisconnectReason.ServerRequirement`, as it does over long-polling, and does not reconnect whatever `ReconnectOptions` say.
- **A dead socket is found by its silence.** A connection lost somewhere on the way often reports nothing, and sends into it still succeed. So every reply has a deadline: the server's `advice.timeout` plus `BayeuxClientOptions.MaxNetworkDelay` for `/meta/connect`, which the server holds, and `MaxNetworkDelay` alone for anything else. Past it the socket is aborted with a `WebSocketException`, and with `ReconnectOptions` the session carries on over a new one. Ten seconds by default; raise it for a slow network, since every miss costs a socket.
- **On .NET Framework, Windows 8 or Windows Server 2012** at least: earlier versions have no WebSocket support.
- **Salesforce** offers long-polling only.

**`BayeuxTransportType.Auto`** tries WebSocket and falls back to long-polling where it does not get through — for a client deployed behind proxies you do not control:

```csharp
new BayeuxClientOptions(channels, "cometd", transportType: BayeuxTransportType.Auto);
```

- **It falls back** when the upgrade is refused, when the server does not offer `websocket`, or when the socket carries the handshake but not the first `/meta/connect` — a proxy that lets the upgrade pass and drops the frames after it.
- **It is not an error.** The client recovers, so nothing reaches `OnError`; the fallback is logged as a warning, and `ActiveTransportType` tells which transport is in use — for a health check or a metric.
- **Once WebSocket has worked, it stays.** After the first answered `/meta/connect` a dropped socket is a network failure, reconnected like any other — not a reason to switch.
- **Each `ConnectAsync` tries WebSocket again**, so a proxy fixed meanwhile is noticed on restart.
- **Both transports' rules apply**: `UseCookies = false`, and an `HttpClient.Timeout` longer than the server's hold, since the client may end up on long-polling.

## Logging

An optional `ILogger<BayeuxClient>` can be passed as the last constructor argument:

```csharp
await using var client = new BayeuxClient(http, options, auth, onDisconnected, logger: myLogger);
```

Omit it and nothing is logged. The library logs session lifecycle and re-handshakes at `Information`, protocol detail at `Debug`, and **never logs request or response bodies** — those carry session identifiers and live data. Errors are not logged at all: they arrive through `OnError` and `OnDisconnected`, and logging them too would make consumers filter the same failure twice.

The dependency is `Microsoft.Extensions.Logging.Abstractions`, referenced at a deliberately low version so it never forces an upgrade on your side.

## Authentication

| You have | Use |
|---|---|
| A server that needs no authentication | `NoAuthProvider.Instance` |
| A session from a login elsewhere, as a cookie | `NoAuthProvider.Instance` and your own `CookieContainer` |
| A user name and password | `HttpBasicAuthProvider` |
| An API key from a gateway | `ApiKeyAuthProvider` |
| A token you obtain and replace yourself | `BearerTokenAuthProvider` |
| An OAuth token that expires — MSAL, client credentials, Salesforce | `RefreshingTokenAuthProvider` |
| Something of your own — a header, several providers at once | `DelegateAuthProvider` |
| Something that keeps state — a signature, a cache | your own `IAuthProvider` |
| Digest, NTLM, Kerberos, a client certificate | `Credentials` or `ClientCertificates` on the `HttpClientHandler` — long-polling only |

**HTTP Basic** — credentials can be replaced at any time, with no reconnect:

```csharp
var auth = new HttpBasicAuthProvider(new BasicAuthCredentials("svc", password));

// later, on password rotation — takes effect on the next request
auth.UpdateCredentials(new BasicAuthCredentials("svc", newPassword));
```

**Bearer token** — OAuth 2.0 access tokens and the like. The provider does not refresh anything: replace the token before it expires, or from `ReconnectOptions.BeforeAttemptAsync` after the server refused it:

```csharp
var auth = new BearerTokenAuthProvider(accessToken);   // the token alone, without "Bearer "

auth.UpdateCredentials(newAccessToken);
```

**Refreshing token** — for a token that expires. Give it a way to obtain one; it fetches on the first request, renews ahead of expiry, and calls your source once however many requests need a token at the same moment:

```csharp
var auth = new RefreshingTokenAuthProvider(async ct =>
{
    var result = await msal.AcquireTokenForClient(scopes).ExecuteAsync(ct);
    return new AccessToken(result.AccessToken, result.ExpiresOn);
});
```

- **Renewed a minute ahead**, or at half the token's life if that is shorter, so a short-lived token does not send every request to the token server.
- **A failed renewal is not an outage** while the token in hand is still valid: it is sent, and the failure reported through `OnCredentialsUpdated`. Only once it has expired does a failure reach the request — and the client's reconnect policy.
- **A token revoked early** — Salesforce reports it as `401::Authentication invalid` — is dropped with `Invalidate()`, and the next attempt fetches a new one:

```csharp
client.OnError += (_, e) =>
{
    if (e.Error is BayeuxConnectException { Error: "401::Authentication invalid" })
        auth.Invalidate();
};

var reconnect = new ReconnectOptions(shouldRetry: e =>
    e is BayeuxConnectException { Error: "401::Authentication invalid" } || ReconnectOptions.IsRetriable(e));
```

**API key** — in a header of its own, `X-API-Key` unless the server wants another name:

```csharp
var auth = new ApiKeyAuthProvider(new ApiKeyCredentials(key, "Ocp-Apim-Subscription-Key"));
```

Credentials are checked when they are created, not when they are sent: a header name `HttpClient` would drop, a key with a line break, a token passed with its `Bearer ` scheme, a user name with a `:` in it are refused right there. `UpdateCredentials` never throws — what it refuses it reports through `OnCredentialsUpdated`, and the credentials in use stay.

Over a WebSocket, credentials go out once per socket, in the upgrade request: a replacement reaches the server with the next socket. Schemes that live in the `HttpClient`'s handler rather than in headers — Digest, NTLM, Kerberos, client certificates — work over long-polling only.

**None** — for servers that need no authentication, and for deployments where a login elsewhere established the session and the cookie is carried in the `CookieContainer` you pass to the constructor:

```csharp
var auth = NoAuthProvider.Instance;
```

**Delegate** — a provider in one lambda, for a header the others do not set, or several providers at once:

```csharp
var auth = new DelegateAuthProvider(request => request.Headers.Add("X-Tenant", tenantId));

var both = new DelegateAuthProvider(async (request, ct) =>
{
    await gatewayKey.ApplyAsync(request, ct);
    await userToken.ApplyAsync(request, ct);
});
```

**Custom** — one method, called before every request including the handshake. This one signs each request with a shared secret, which none of the built-in providers does:

```csharp
public sealed class SignedRequestAuthProvider : IAuthProvider
{
    private readonly string _keyId;
    private readonly byte[] _secret;

    public SignedRequestAuthProvider(string keyId, byte[] secret) => (_keyId, _secret) = (keyId, secret);

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var hmac = new HMACSHA256(_secret);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(_keyId + ":" + timestamp)));

        request.Headers.TryAddWithoutValidation("X-Key-Id", _keyId);
        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Signature", signature);
        return Task.CompletedTask;
    }
}
```

A provider must support replacing its credentials **in place** — the client resolves it once and holds that instance for its lifetime, so a provider that needs reconstruction cannot rotate credentials without tearing down the session. Any field written by callers and read by `ApplyAsync` must be `volatile`. Implement `IAuthProvider<TCredentials>` to expose a typed `UpdateCredentials` and an `OnCredentialsUpdated` event.

## Extensions (`ext`)

Every Bayeux message may carry an `ext` object — the protocol's own extension point. Acknowledgement, time sync, Salesforce's durable replay and authentication inside the message all work through it. An extension usually negotiates: it asks in the handshake, and acts only once the server agrees in the reply.

Implement `IBayeuxExtension` and pass it in the options. This one puts credentials where a CometD server with a custom `SecurityPolicy` reads them — the exact shape is whatever your server expects:

```csharp
public sealed class ExtAuthentication : IBayeuxExtension
{
    private readonly string _user;
    private readonly string _token;

    public ExtAuthentication(string user, string token) => (_user, _token) = (user, token);

    public Task OutgoingAsync(BayeuxRequestModel requestModel, CancellationToken cancellationToken)
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

var options = new BayeuxClientOptions(channels, "cometd", extensions: [new ExtAuthentication("svc", token)]);
```

What the client guarantees:

- **`OutgoingAsync` runs for every message, before it is serialised** — once per message, not per HTTP request: five subscriptions in one batch are five calls, and one `IAuthProvider.ApplyAsync`. It is asynchronous because an extension may have to obtain something first, such as a fresh token — pass the `cancellationToken` to whatever it awaits, so that a stop or a cancelled call is not held up by an extension's I/O. A cancellation it causes is not reported as a failure.
- **If it throws, the message is not sent.** The operation fails, and `OnError` reports it once, as `ErrorSource.Ext`.
- **`Incoming` runs for every message the server sends back, before the client acts on it and before any handler.** An extension therefore sees an event even when the handler for it throws. If `Incoming` throws, the failure is reported and the message is still delivered.
- **Both are called concurrently** — from the connect loop and from your own subscribe calls. Keep extension state thread-safe.
- **No extensions, no `ext`.** Without one registered, the client sends exactly what it would send if the field did not exist.

Meta channel names — `/meta/handshake`, `/meta/connect` and the rest — are fixed by the protocol, and an extension compares them as plain strings.

**Salesforce durable streaming** — resuming after a dropped session, or even after a restart of your application, from the last event received — is in `Samples/`: `SalesforceReplayExtension` uses both directions, and `SalesforceReplayExample` shows it with a bearer token and positions saved to a file. It is a sample rather than part of the package because it is vendor-specific; copy it into your project.

## Handlers

A handler receives the event and a cancellation token, and returns a task:

```csharp
["/topic/**"] = async (e, cancellationToken) =>
    await store.SaveAsync(e.Channel, e.Data, cancellationToken)
```

`e.Channel` is the concrete channel the event arrived on — for a pattern like `/topic/**`, which of the matching channels it was. `e.Data` is the payload, `e.Ext` any extension data. A handler with nothing to await returns `Task.CompletedTask`.

**Handlers are awaited.** The next long poll is not sent until they finish, so a slow handler delays every event behind it while the server holds them. A handler that runs longer than the server's session timeout — CometD's `maxInterval`, 10 seconds by default — lets the session expire, and whatever the server was holding is lost. Keep handlers short, and hand long work to a queue of your own. Over a [WebSocket](#transports) the connect does not wait for handlers, so the session is not at risk; events still run one at a time, and a slow handler still delays the ones behind it.

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

## Lifecycle

| | |
|---|---|
| `ConnectAsync()` | Stops any current session, handshakes, subscribes, starts polling. Throws on failure. |
| `DisconnectAsync()` | Stops the loop, waits for it, releases the session. Safe when nothing is running. |
| `DisposeAsync()` | As above, plus releases resources. Shutdown has completed when it returns. |
| `Dispose()` | Signals and returns immediately — the loop may still send `/meta/disconnect` and raise the disconnect event afterwards. |

`ConnectAsync` always disconnects first, so calling it twice is safe. The lifecycle is **not thread-safe**; drive it from a single thread.

### Cancellation

Every call takes an optional `CancellationToken`. It stops **that call** from waiting — it never decides how long the session lives. The session belongs to the client and ends only through `DisconnectAsync`, disposal, or the server.

| | What the token bounds | If it is cancelled |
|---|---|---|
| `ConnectAsync` | Setting up: handshake and subscriptions | Nothing is left half-open on the server; `OperationCanceledException` is thrown. Once connected, the token no longer matters — cancelling or disposing it later does not touch the session. |
| `SubscribeNewChannelsAsync` | The wait for the channel lock and for the server's answer | The whole batch is rolled back, as for any request whose outcome is unknown. Nothing from it is subscribed. |
| `UnsubscribeChannelsAsync` | The same | Every handler in the batch stays registered: the server may still consider those subscriptions live. |
| `DisconnectAsync` | Only the wait for the loop to finish | The stop itself cannot be cancelled. It is signalled before the wait begins, and the loop goes on stopping in the background. |
| `PublishAsync` | The wait for the server's reply | Nothing is taken back: a message already sent may still be delivered. Never retried. |

```csharp
// Give up on connecting after 10 seconds - the session, once up, is not affected.
using (var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    await client.ConnectAsync(connectTimeout.Token);

// On shutdown, wait at most 5 seconds for a slow handler; the client stops regardless.
using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try { await client.DisconnectAsync(shutdown.Token); }
catch (OperationCanceledException) { /* still stopping in the background */ }
```

Handlers and extensions receive the **session's** token, not the caller's. It is cancelled when the client stops, and a cancellation it causes is not reported as an error. Nor is the stop itself: the `/meta/connect` it calls off does not reach `OnError`.

`ClientId` is non-empty only while a session is fully established — handshake **and** subscriptions. It empties and fills again on every re-handshake inside the connect loop, so an empty value on a running client is normal recovery rather than a failure, and a runtime `SubscribeNewChannelsAsync` landing in that window is refused. Use it to correlate with server logs, not as a connection flag: it is written by the client's task and read by yours. Whether the client is connected is what `State` is for — see [Events](#events).

## Events

`State` says where the client is, and `OnStateChanged` reports every change:

```
Disconnected → Connecting → Connected ⇄ Reconnecting
     ↑__________________________________________|     (from any of them, when the client stops)
```

```csharp
client.OnStateChanged += (_, e) =>
    log.Info($"{e.PreviousState} -> {e.CurrentState} {e.Error?.Message}");
```

- **`Connected` means a `/meta/connect` succeeded**, not merely that `ConnectAsync` returned; until the first poll is answered the state is still `Connecting`. Servers answer the first poll of a session at once, so the gap is short.
- **`Reconnecting`** covers both the server asking for a new handshake and an error retried by `ReconnectOptions`; `Error` carries the error, if there was one.
- **`Disconnected` comes before `OnDisconnected`**, so a handler that reconnects from that event starts from a consistent state.
- The event runs on the thread making the change — usually the connect loop — under the same rules as a message handler: keep it quick; `DisconnectAsync` only signals, `ConnectAsync` throws. Exceptions from subscribers are swallowed.

The states map directly onto a health check: `Connected` healthy, `Reconnecting` degraded, the rest unhealthy.

`OnDisconnected` fires **once**, when the loop stops — and only if a session was established. A failed `ConnectAsync` throws instead.

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

`OnError` fires for each error observed, with the operation that produced it and whether the client is about to stop:

```csharp
client.OnError += (_, e) =>
{
    log.Warn($"{e.Source}: {e.Error.Message} (fatal: {e.IsFatal})");
};
```

Non-fatal sources include `Handler` — an exception from your own message handler, swallowed so a bug there cannot stop the client — and `Connect` carrying CometD's `multiple-clients` advice, which means several clients are sharing cookie state and all but one have been demoted to interval polling.

## Reconnecting

**Nothing is retried unless you ask.** By default any error ends the session: one dropped connection stops the client, and the library never hides a failure by quietly recovering from it. (The one exception is the protocol's own: when the server invalidates a session with `402` or `advice.reconnect: "handshake"`, the client handshakes again and resubscribes, because the server asked it to.)

To have the client re-establish a session after an error, pass `ReconnectOptions`:

```csharp
var options = new BayeuxClientOptions(channels, "cometd", reconnectOptions: new ReconnectOptions());
```

After a failure the client waits and tries again. **A transport failure keeps the session**: no answer, a timeout, an HTTP error or a dropped WebSocket says nothing about the session, which the server holds until its `maxInterval` with every event published meanwhile queued in it — so the client retries `/meta/connect` with the same `ClientId` and loses nothing that was still queued. If the session did expire, the server answers `402` and the client handshakes again. **A refusal ends the session**: after a refused handshake or connect the client handshakes again and resubscribes every channel. Attempt *n* waits a random time between zero and `min(MaxDelay, InitialDelay × Multiplier^(n−1))` — one second doubling up to thirty by default. The bound grows so that a server that is down is not hammered; the randomness ("full jitter") keeps a thousand clients dropped by one restart from all coming back in the same millisecond. The count starts again once a `/meta/connect` succeeds.

**Only errors that can clear by themselves are retried.** `ReconnectOptions.IsRetriable`, the default, retries a lost connection, a timeout, HTTP 5xx, 408 and 429, and a refused handshake or connect whose advice invites another. It does **not** retry 401, 403, or a handshake or connect refused with `advice.reconnect: "none"`: repeating a rejected login every few seconds is how a service account gets locked. Failures are thrown as typed exceptions, so your own predicate can tell them apart:

| Exception | When | Carries |
|---|---|---|
| `BayeuxHttpException` | a status other than success | `StatusCode`, on every target framework |
| `BayeuxHandshakeException` | `/meta/handshake` answered `successful: false` | the server's `Error`, `Reconnect` advice and `Ext` |
| `BayeuxConnectException` | `/meta/connect` answered `successful: false` with advice `none` | the same three |

**A refusal is not a clean stop.** A server that ends a session successfully — a successful reply advising `none`, or a server-sent `/meta/disconnect` — stops the client with `DisconnectReason.ServerRequirement` and no error. A *refused* connect stops it with `Failed` and a `BayeuxConnectException`, so the reason is never lost, and a policy may retry it.

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
- **`OnError` reports each failure with `IsFatal == false`**, because the client carries on. `OnDisconnected` fires only when it gives up — `Reason == Failed`, with the last error — or when you stop it.
- **`DisconnectAsync` interrupts a pending delay**, and nothing is attempted afterwards. Nothing is sent for a session already lost to a refusal; a session kept after a transport failure is closed as usual — which, if the server is still unreachable, waits up to `DisconnectTimeout`.
- **`BeforeAttemptAsync` runs before every attempt**, a retried connect as much as a new handshake: credentials can expire either way.

### Reconnecting by hand

Without `ReconnectOptions` the decision stays yours. The disconnect handler runs after the loop has finished, so it may reconnect directly:

```csharp
onDisconnected: async (_, e) =>
{
    if (e.Reason == DisconnectReason.ServerRequirement)
        return;                                   // the server asked us to stop

    await Task.Delay(TimeSpan.FromSeconds(5));    // pace it yourself
    await client.ConnectAsync();
}
```

Without a delay this retries as fast as the server can refuse. Exceptions thrown by the handler are swallowed.

## Acknowledgements

By default delivery is at-most-once: if the reply carrying events is lost — the connection drops after the server sent it — those events are gone. The acknowledge extension makes the server keep events until the client confirms them:

```csharp
new BayeuxClientOptions(channels, "cometd",
    extensions: [new BayeuxAckExtension()],
    reconnectOptions: new ReconnectOptions());
```

The server numbers each reply that carries events; the next `/meta/connect` tells it the last number received, and until then the events stay queued. When a reply is lost, the client — with `ReconnectOptions` — retries the connect in the same session, still confirming the earlier batch, and the server sends the missing events again. Without `ReconnectOptions` the first failure stops the client and the extension has nothing to recover.

- **Acknowledged means handled.** The confirmation goes out with the next connect, which the client sends only after every handler for the previous events has returned.
- **At-least-once within a session.** An event can arrive twice — its reply arrived, but the confirmation did not — so handlers should tolerate duplicates. A session that ends, by expiry or a new handshake, still loses its queue.
- **The server must support it.** `IsAckSupported` says whether it agreed in the last handshake; a server that did not sees nothing different. With acknowledgements on, CometD delivers events only in `/meta/connect` replies.

## Timesync

How far the server's clock is from this machine's, and how long a message takes on the network — for an application that acts on the server's time:

```csharp
var timesync = new BayeuxTimesyncExtension();

new BayeuxClientOptions(channels, "cometd", extensions: [timesync]);

var serverNow = timesync.ServerTime;   // this machine's time, corrected by timesync.Offset
```

- **Every handshake and connect carries the client's clock**, and the server answers with its own and how long it held the message. The hold is left out of the network delay, so a `/meta/connect` held for the server's whole timeout does not pass for a slow network.
- **Averaged** over the last ten samples by default — `new BayeuxTimesyncExtension(maxSamples: 20)` for more.
- **The server must support it** — CometD's `TimesyncExtension`. Until it has answered, `IsTimesyncSupported` is `false`, `Offset` and `Lag` are zero, and `ServerTime` is the local time. A reply it cannot read is skipped, not reported.

## Protocol provenance

Written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux). Message field names, the `/meta/` channel semantics and the advice-driven reconnect behaviour all follow the specification text. No code is derived from any other Bayeux implementation.

## Licence

Apache License 2.0 — see [LICENSE](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/LICENSE) and [NOTICE](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/NOTICE).

Contributions require a Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/CONTRIBUTING.md) and [DCO](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/DCO).
