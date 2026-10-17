# Authentication

Which provider to use, and how to write your own.

[← Back to the README](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md) · [All documentation](https://github.com/naleksashenkov/Bayeux.Client.Extensible/blob/main/README.md#documentation)

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
