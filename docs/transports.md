# Transports and the HttpClient

How messages travel, what the `HttpClient` you pass must look like, and logging.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

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
- **Events run in order, apart from the connect.** A handler sees a channel's events in the order the server sent them, one after another, as with long-polling. But the next `/meta/connect` does not wait for them — except with [acknowledgements](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/extensions.md#acknowledgements), where CometD sends events only in connect replies.
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
