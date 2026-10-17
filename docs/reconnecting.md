# Reconnecting

What the client retries, how long it waits, and what a lost session means.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

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
