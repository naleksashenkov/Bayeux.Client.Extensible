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

**`UseCookies = false` is required**: the client keeps each session's cookies itself. That, and the timeout, are the two things to get right — see [the HttpClient contract](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/transports.md#the-httpclient-contract).

## Documentation

| | |
|---|---|
| [Transports and the HttpClient](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/transports.md) | long-polling, WebSocket, `Auto`; the `HttpClient` contract; logging |
| [Authentication](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/authentication.md) | which provider to use — Basic, bearer, refreshing token, API key, delegate — and your own |
| [Handlers, channels and publishing](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/handlers-and-channels.md) | handlers, typed data, wildcards, subscribing at runtime, publishing |
| [Lifecycle and events](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/lifecycle-and-events.md) | connect, disconnect, dispose, cancellation, state and error events |
| [Reconnecting](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/reconnecting.md) | backoff and jitter, what is retried, sessions kept across network failures |
| [Extensions](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/extensions.md) | `ext`, acknowledgements, timesync |
| [Migrating from CometD.NetCore2](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/docs/migrating-from-cometd-netcore2.md) | the other library's API, mapped onto this one |

Runnable examples for each of them are in [`Samples/`](https://github.com/naleksashenkov/Bayeux.Client.Extensible/tree/main/Samples/Bayeux.Client.Extensible.Samples).

## Protocol provenance

Written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux). Message field names, the `/meta/` channel semantics and the advice-driven reconnect behaviour all follow the specification text. No code is derived from any other Bayeux implementation.

## Licence

Apache License 2.0 — see [LICENSE](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/LICENSE) and [NOTICE](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/NOTICE).

Contributions require a Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/CONTRIBUTING.md) and [DCO](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/DCO).
