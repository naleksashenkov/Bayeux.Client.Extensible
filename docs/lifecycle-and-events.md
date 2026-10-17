# Lifecycle and events

Connecting, stopping, cancellation, and what the client reports while it runs.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

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
