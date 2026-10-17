# Migrating from CometD.NetCore2

[CometD.NetCore2](https://github.com/kdcllc/CometD.NetCore) is a port of the Java CometD client and,
with about 1.2 million downloads, the most used Bayeux client for .NET. This page maps its API onto
this library's, for code moving from one to the other.

The two differ less in what they do than in how: CometD.NetCore2 keeps the Java client's shape —
synchronous calls, listeners, a transport built from an options dictionary — while this library is
`async` throughout and takes its HTTP from your `HttpClient`.

## At a glance

| | CometD.NetCore2 2.0.8 | Bayeux.Client.Extensible |
|---|---|---|
| Transports | long-polling | long-polling, WebSocket, `Auto` (WebSocket with fallback) |
| HTTP | `HttpWebRequest`, `Begin`/`End` callbacks | your `HttpClient`, `async`/`await` |
| Handlers | `IMessageListener.OnMessage`, synchronous | `BayeuxEventHandler`, asynchronous, with a cancellation token |
| Authentication | headers fixed when the transport is created | `IAuthProvider`, replaced at runtime; Basic, bearer, refreshing OAuth token, API key, delegate |
| Reconnect | follows the server's advice | follows the advice; on failures, opt-in `ReconnectOptions` with backoff, jitter and a session kept across network failures |
| JSON | Newtonsoft.Json | System.Text.Json; typed data with `GetData<T>` |
| Acknowledgements | `AckExtension` | `BayeuxAckExtension` |
| Salesforce replay | `ReplayExtension`, `GetChannel(name, replayId)` | a sample today (`Samples/SalesforceReplayExtension.cs`); a shipped extension is planned |
| Timesync | `TimesyncClientExtension` | `BayeuxTimesyncExtension` |
| Targets | netstandard2.0, netstandard2.1, net7.0 | netstandard2.0, net472, net10.0 |
| Last release | January 2023 | — |

## Step by step

### The client

**Before** — the endpoint as a URL, the transport built from options and headers:

```csharp
var transport = new LongPollingTransport(
    new Dictionary<string, object> { [ClientTransport.TIMEOUT_OPTION] = 120_000 },
    new NameValueCollection { ["Authorization"] = "OAuth " + accessToken });

var client = new BayeuxClient(instanceUrl + "/cometd/59.0", transport);
```

**After** — the address and the timeout from an `HttpClient` you own, credentials from a provider:

```csharp
using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
{
    BaseAddress = new Uri(instanceUrl),
    Timeout = TimeSpan.FromMinutes(2)
};

var auth = new BearerTokenAuthProvider(accessToken);

await using var client = new BayeuxClient(
    http,
    new BayeuxClientOptions(channels, "cometd/59.0"),
    auth,
    onDisconnected: (_, e) => logger.LogInformation("Stopped: {Reason}", e.Reason));
```

- **`UseCookies = false` is required**: the client keeps each session's cookies itself, so several
  clients can share one `HttpClient`. See [The HttpClient contract](https://github.com/naleksashenkov/Bayeux.Client.Extensible#the-httpclient-contract).
- **A token that expires** no longer means rebuilding the transport: `auth.UpdateCredentials(newToken)`,
  or `RefreshingTokenAuthProvider` to renew it without your code.
- **Cookies** set with `SetCookie` go in a `CookieContainer` passed to the constructor instead.

### Connecting

**Before:**

```csharp
client.Handshake();
client.WaitFor(10_000, new[] { BayeuxClient.State.CONNECTED });
```

**After:**

```csharp
await client.ConnectAsync(cancellationToken);
```

`ConnectAsync` handshakes, subscribes to every channel in the options, and starts the connect loop;
it throws if any of that fails, so there is no state to poll. Afterwards, `State` and
`OnStateChanged` say how the session is doing.

### Subscribing

**Before** — a listener class per handler, called synchronously:

```csharp
client.GetChannel("/event/Order__e").Subscribe(new OrderListener());

class OrderListener : IMessageListener
{
    public void OnMessage(IClientSessionChannel channel, IMessage message) =>
        Handle(JsonConvert.DeserializeObject<Order>(message.Json));
}
```

**After** — a handler per channel, asynchronous, data typed if you like:

```csharp
var channels = new Dictionary<string, BayeuxEventHandler>
{
    ["/event/Order__e"] = BayeuxHandler.Of<Order>(async (order, e, ct) => await HandleAsync(order, ct))
};
```

- Channels known up front go in the options and are subscribed by `ConnectAsync` — and again after
  every re-handshake, automatically.
- Channels added later: `await client.SubscribeNewChannelsAsync(...)`, several in one batch.
- `message.Json` / `DataAsDictionary` become `e.Data`, a `JsonElement`, or `e.GetData<T>()`.
  Names are camelCase by default (`JsonSerializerDefaults.Web`); pass your own
  `JsonSerializerOptions` in the options to change it.
- A handler is awaited before the next poll. Long work belongs in a queue of your own; see
  [Handlers](https://github.com/naleksashenkov/Bayeux.Client.Extensible#handlers).

### Unsubscribing and publishing

| Before | After |
|---|---|
| `client.GetChannel(name).Unsubscribe()` | `await client.UnsubscribeChannelsAsync([name])` |
| `client.GetChannel(name).Publish(data)` | `await client.PublishAsync(name, data)` — completes when the server accepted it, throws `BayeuxPublishException` when it refused |

### Extensions

**Before:** `client.AddExtension(new MyExtension())`, implementing `IExtension`.

**After:** pass them in the options — `new BayeuxClientOptions(channels, path, extensions: [new MyExtension()])` —
implementing `IBayeuxExtension`: `OutgoingAsync` for every message sent, `Incoming` for every message
received. `AckExtension` becomes `BayeuxAckExtension`.

**Salesforce replay** — `ReplayExtension` and `GetChannel(name, replayId)` — has no shipped
counterpart yet. [`Samples/SalesforceReplayExtension.cs`](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/Samples/Bayeux.Client.Extensible.Samples/SalesforceReplayExtension.cs)
implements it, positions saved across restarts included, and can be copied as it is.

### Errors and reconnecting

**Before:** listeners on the meta channels — `/meta/handshake`, `/meta/connect` — and the client
following the server's advice.

**After:**

- `OnError` reports every failure with its source and whether it ended the session.
- `OnDisconnected` reports why the client stopped.
- `OnStateChanged` reports `Connecting`, `Connected`, `Reconnecting`, `Disconnected`.

The server's advice is followed as before: a `402` or `reconnect: handshake` leads to a new handshake
and every subscription restored. **What differs is failures.** By default one dropped connection
stops the client, so that nothing is hidden. To retry, pass `ReconnectOptions`:

```csharp
new BayeuxClientOptions(channels, "cometd/59.0", reconnectOptions: new ReconnectOptions());
```

It retries with growing, randomised delays, keeps the session across network failures — so nothing
queued on the server is lost — and never retries a refused login. See
[Reconnecting](https://github.com/naleksashenkov/Bayeux.Client.Extensible#reconnecting).

### Disconnecting

**Before:**

```csharp
client.Disconnect();
client.WaitFor(5_000, new[] { BayeuxClient.State.DISCONNECTED });
```

**After:** `await client.DisconnectAsync()`, or let `await using` dispose the client.

## Moving to WebSocket

Once the code runs over long-polling, WebSocket is one option:

```csharp
new BayeuxClientOptions(channels, path, transportType: BayeuxTransportType.Auto);
```

`Auto` uses WebSocket where it gets through and long-polling where a proxy or the server blocks it.
Salesforce offers long-polling only. See
[Transports](https://github.com/naleksashenkov/Bayeux.Client.Extensible#transports).
