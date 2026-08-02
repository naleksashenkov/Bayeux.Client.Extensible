# Bayeux.Client.Extensible

**The existing .NET Bayeux clients don't support authentication.** They implement the protocol — handshake, subscribe, long-poll — and then assume the server will simply talk to you. Real deployments rarely do. Salesforce CometD wants an OAuth bearer token on every request and a fresh one when it expires mid-session; other endpoints want a session cookie obtained from a separate login call, or a signed header, or mutual TLS. Bolting any of that onto a client that has no seam for it means forking it or wrapping `HttpClient` and hoping the handshake still works after a 401.

This library adds that seam. Authentication is a first-class concept: a single `IAuthProvider` interface the client consults when it builds a request and again when the server rejects one, so a token can be attached, refreshed and retried without the transport or the protocol layer knowing anything about how credentials are obtained.

Everything else is a conforming Bayeux client — written from the Bayeux protocol specification, not derived from any existing implementation.

## Status

⚠️ **Early development.** The protocol core and the authentication strategies below are the intended surface, not a shipped API. Names and signatures in this README are the design being built toward and will change. Treat the code samples as the target, not as documentation of working behaviour.

## Install

```bash
dotnet add package Bayeux.Client.Extensible
```

Targets .NET 10.0.

## Quick start

```csharp
await using var client = new BayeuxClient("https://example.com/cometd", new BearerTokenAuthProvider("my-token"));
await client.ConnectAsync();
await client.SubscribeAsync("/topic/Orders", msg => Console.WriteLine(msg.Data));
await Task.Delay(Timeout.Infinite);
```

## Authentication strategies

Each bundled strategy implements `IAuthProvider`. Pass one to the client; it handles attaching credentials and recovering from rejection.

**Static bearer token** — a token you already hold:

```csharp
var auth = new BearerTokenAuthProvider("eyJhbGciOi...");
```

**Refreshable bearer token** — a callback invoked on first use and again whenever the server answers 401:

```csharp
var auth = new RefreshingTokenAuthProvider(async ct => await tokenService.GetAccessTokenAsync(ct));
```

**Basic authentication** — username and password, sent as an `Authorization: Basic` header:

```csharp
var auth = new BasicAuthProvider("service-account", password);
```

**Cookie session** — performs a login request, then carries the resulting cookies on every Bayeux request:

```csharp
var auth = new CookieSessionAuthProvider(
    loginUri: new Uri("https://example.com/login"),
    credentials: new NetworkCredential("service-account", password));
```

**No authentication** — for servers that need none, and the default when no provider is supplied:

```csharp
var auth = NullAuthProvider.Instance;
```

## Writing a custom IAuthProvider

Implement the interface when none of the bundled strategies fit — a signed header, a vendor handshake, mutual TLS:

```csharp
public sealed class HmacAuthProvider : IAuthProvider
{
    private readonly byte[] _key;

    public HmacAuthProvider(byte[] key) => _key = key;

    /// <summary>Called before every request so credentials can be attached.</summary>
    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var signature = Convert.ToBase64String(
            HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(request.RequestUri!.PathAndQuery)));
        request.Headers.Add("X-Signature", signature);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called when the server rejects a request. Return true to have the client
    /// re-apply credentials and retry once; false to surface the failure.
    /// </summary>
    public ValueTask<bool> OnUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => ValueTask.FromResult(false); // nothing to refresh — a bad signature will not fix itself
}
```

Two methods carry the whole contract: `ApplyAsync` decorates an outgoing request, and `OnUnauthorizedAsync` decides whether a rejection is recoverable. A provider that refreshes a token returns `true` after obtaining a new one; a provider with fixed credentials returns `false` so the caller sees the error instead of retrying forever.

Providers are consulted on every request including the initial handshake, so a client whose token expires during a long-lived subscription recovers without dropping the session.

## Protocol provenance

This client is written from the [Bayeux protocol specification](https://docs.cometd.org/current/reference/#_bayeux). Message field names, the `/meta/` channel semantics, the advice-driven reconnect behaviour and the transport negotiation all follow the specification text. No code is derived from any other Bayeux implementation.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Contributions require a Developer Certificate of Origin sign-off; see [CONTRIBUTING.md](CONTRIBUTING.md) and [DCO](DCO).
