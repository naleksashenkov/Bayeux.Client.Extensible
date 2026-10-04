# Changelog

All notable changes to this project are recorded here — what a user needs to know before
upgrading, not every commit. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Releasing:** rename the `Unreleased` heading below to `## [x.y.z] - YYYY-MM-DD`, add a fresh empty
`Unreleased` above it, move `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`, then push the
tag `vx.y.z`. The release workflow refuses to publish a version that has no section here.

## [Unreleased]

### Added

- `BayeuxClient`, a Bayeux long-polling client written from the protocol specification:
  handshake, subscribe, unsubscribe, the `/meta/connect` poll loop and advice-driven
  re-handshake, and disconnect.
- `IAuthProvider`, consulted before every request including the handshake, so credentials can be
  attached and replaced at runtime without the protocol layer knowing where they came from.
- `HttpBasicAuthProvider` with in-place credential replacement, and `NoAuthProvider`.
- Asynchronous handlers: a `BayeuxEventHandler` receives a `BayeuxEvent` - the concrete channel,
  the data and any `ext` - with a cancellation token, and is awaited before the next long poll.
  Its exceptions are reported as `ErrorSource.Handler` without stopping the client. From inside a
  handler `DisconnectAsync` only signals, and `ConnectAsync` is refused rather than left to hang.
- An optional `CancellationToken` on `ConnectAsync`, `SubscribeNewChannelsAsync`,
  `UnsubscribeChannelsAsync` and `DisconnectAsync`. It bounds the call, never the session: a
  cancelled connect leaves nothing open on the server, a cancelled subscribe or unsubscribe rolls
  its batch back, and a cancelled disconnect stops waiting while the client still stops.
- Wildcard subscriptions: `/a/*` for one further segment, `/a/**` for any depth below, with
  overlapping patterns each receiving the message, as CometD does.
- Batched subscribe and unsubscribe: a whole set of channels travels as one Bayeux message.
  A partial rejection raises `BayeuxSubscriptionException` naming both the accepted and the
  rejected channels; a transport failure rolls the entire batch back and propagates unchanged.
- `PublishAsync`: one message to an application channel, sent to the base path as CometD
  clients do. A refusal throws `BayeuxPublishException`; failures go to the caller, not
  `OnError`, and a publish is never retried. The reply to a publish is not delivered as an event,
  and a subscriber receives its own messages, as CometD delivers them.
- Cookie isolation per client, so several clients can share one `HttpClient` without their
  sessions collapsing into one.
- Each client owns its subscription list, `IBayeuxClient.Channels`. `BayeuxClientOptions.Channels` is
  only the starting set, so several clients can share one options object - a DI singleton, say -
  without seeing each other's subscribe and unsubscribe.
- Optional `ILogger` support. Nothing is logged when none is supplied, and request and response
  bodies are never logged.
- `ext` on every message, and `IBayeuxExtension` to read and write it: outgoing before serialisation,
  incoming before the client and any handler. Registered through `BayeuxClientOptions`; a failing
  extension is reported as `ErrorSource.Ext`.
- Opt-in reconnection through `BayeuxClientOptions.ReconnectOptions`: after an error the client waits -
  exponential backoff with full jitter - then tries again. After a transport failure it retries
  `/meta/connect` in the same session, keeping whatever the server queued meanwhile; after a
  refusal it handshakes and resubscribes. Only errors that can clear by themselves are retried by
  default (`ReconnectOptions.IsRetriable`); 401, 403 and a handshake the server advises against
  repeating are not. `ShouldRetry` replaces that rule,
  `BeforeAttemptAsync` refreshes credentials before each attempt, and `MaxAttempts` caps them.
  `ConnectAsync` itself is never retried.
- Client state: `IBayeuxClient.State` - `Disconnected`, `Connecting`, `Connected`, `Reconnecting` - and
  `OnStateChanged`, raised once per real change with the error that caused it. `Connected` follows
  the first successful `/meta/connect`, not `ConnectAsync` alone; `Disconnected` comes before
  `OnDisconnected`. Subscribers run under the same rules as message handlers.
- Typed data: `BayeuxHandler.Of<T>` and `BayeuxEvent.GetData<T>()` read an event's data as an
  object, and `PublishAsync` writes one, both with `BayeuxClientOptions.JsonSerializerOptions` -
  `JsonSerializerDefaults.Web`, camelCase, by default.
- `BayeuxAckExtension`: the acknowledge extension. The server keeps events until the client
  confirms them and sends them again if a reply was lost, making delivery at-least-once within a
  session. Confirmation follows the handlers, so acknowledged means handled.
- `BayeuxHttpException`, carrying the HTTP status on every target framework, and
  `BayeuxHandshakeException` and `BayeuxConnectException`, carrying the server's error, reconnect
  advice and `ext`. A connect the server *refuses* with advice `none` now ends the session as
  `Failed` with a `BayeuxConnectException`, so its reason reaches the caller; only a clean stop - a
  successful reply advising `none`, or a server-sent `/meta/disconnect` - is `ServerRequirement`.
  This is how Salesforce reports a revoked access token.
- Salesforce durable replay as a sample in `Samples/`, including positions saved across restarts.
- Multi-targeting: `net10.0`, `netstandard2.0` and `net472`.
- Source Link, deterministic CI builds and a symbol package.

### Known limitations

- Long-polling only; there is no WebSocket transport.
- A session that ends - by expiry or a new handshake - loses whatever was still queued for it on
  the server, with or without acknowledgements.

[Unreleased]: https://github.com/naleksashenkov/Bayeux.Client.Extensible/commits/main
