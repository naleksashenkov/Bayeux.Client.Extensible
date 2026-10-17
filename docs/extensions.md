# Extensions

The protocol's `ext` field, acknowledgements and timesync.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

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
