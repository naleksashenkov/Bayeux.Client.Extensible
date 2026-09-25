# Changelog

All notable changes to this project are recorded here — what a user needs to know before
upgrading, not every commit. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Releasing:** rename the `Unreleased` heading below to `## [x.y.z] - YYYY-MM-DD`, add a fresh empty
`Unreleased` above it, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, then push the
tag `vx.y.z`. The release workflow refuses to publish a version that has no section here.

## [Unreleased]

### Added

- `CometDPoller`, a Bayeux long-polling client written from the protocol specification:
  handshake, subscribe, unsubscribe, the `/meta/connect` poll loop and advice-driven
  re-handshake, and disconnect.
- `IAuthProvider`, consulted before every request including the handshake, so credentials can be
  attached and replaced at runtime without the protocol layer knowing where they came from.
- `HttpBasicAuthProvider` with in-place credential replacement, and `NoAuthProvider`.
- Asynchronous handlers: a `BayeuxEventHandler` receives a `BayeuxEvent` - the concrete channel,
  the data and any `ext` - with a cancellation token, and is awaited before the next long poll.
  Its exceptions are reported as `ErrorSource.Handler` without stopping the poller. From inside a
  handler `DisconnectAsync` only signals, and `ConnectAsync` is refused rather than left to hang.
- An optional `CancellationToken` on `ConnectAsync`, `SubscribeNewChannelsAsync`,
  `UnsubscribeChannelsAsync` and `DisconnectAsync`. It bounds the call, never the session: a
  cancelled connect leaves nothing open on the server, a cancelled subscribe or unsubscribe rolls
  its batch back, and a cancelled disconnect stops waiting while the poller still stops.
- Wildcard subscriptions: `/a/*` for one further segment, `/a/**` for any depth below, with
  overlapping patterns each receiving the message, as CometD does.
- Batched subscribe and unsubscribe: a whole set of channels travels as one Bayeux message.
  A partial rejection raises `BayeuxSubscriptionException` naming both the accepted and the
  rejected channels; a transport failure rolls the entire batch back and propagates unchanged.
- Cookie isolation per poller, so several pollers can share one `HttpClient` without their
  sessions collapsing into one.
- Optional `ILogger` support. Nothing is logged when none is supplied, and request and response
  bodies are never logged.
- `ext` on every message, and `IBayeuxExt` to read and write it: outgoing before serialisation,
  incoming before the poller and any handler. Registered through `CometdPollerOptions`; a failing
  extension is reported as `ErrorSource.Ext`.
- Salesforce durable replay as a sample in `Samples/`, including positions saved across restarts.
- Multi-targeting: `net10.0`, `netstandard2.0` and `net472`.
- Source Link, deterministic CI builds and a symbol package.

### Known limitations

- Long-polling only; there is no WebSocket transport.
- No automatic retry. Any error ends the session, and reconnecting is the caller's decision.
- Delivery is at-most-once: the acknowledge extension is not implemented, so a session break
  loses whatever was still queued on the server.
- The client cannot publish to a channel yet.

[Unreleased]: https://github.com/naleksashenkov/Bayeux.Client.Extensible/commits/main
