// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// The built-in providers: what each puts on a request, what it refuses up front, and that a
/// refused replacement leaves the working credentials in place.
/// </summary>
public class AuthProviderTests
{
    private static async Task<HttpRequestMessage> ApplyAsync(IAuthProvider provider)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/cometd");
        await provider.ApplyAsync(request, CancellationToken.None);
        return request;
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? Assert.Single(values) : null;

    // Every UpdateCredentials outcome, in order.
    private static ConcurrentQueue<OnCredentialsUpdatedEventArgs> Outcomes<T>(IAuthProvider<T> provider)
    {
        var outcomes = new ConcurrentQueue<OnCredentialsUpdatedEventArgs>();
        provider.OnCredentialsUpdated += (_, e) => outcomes.Enqueue(e);
        return outcomes;
    }

    // ---- API key -------------------------------------------------------------------------------

    [Fact]
    public async Task An_api_key_travels_in_its_own_header_not_in_authorization()
    {
        var request = await ApplyAsync(new ApiKeyAuthProvider(new ApiKeyCredentials("k-1")));

        Assert.Equal("k-1", Header(request, "X-API-Key"));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task The_header_name_is_the_servers_choice()
    {
        var request = await ApplyAsync(new ApiKeyAuthProvider(new ApiKeyCredentials("k-1", "Ocp-Apim-Subscription-Key")));

        Assert.Equal("k-1", Header(request, "Ocp-Apim-Subscription-Key"));
        Assert.Null(Header(request, "X-API-Key"));
    }

    [Theory]
    [InlineData("X API Key")]      // not a valid header name
    [InlineData("Content-Type")]   // content, which a request's own headers cannot carry
    [InlineData("")]
    [InlineData("  ")]
    public void A_header_that_could_not_be_sent_is_refused_up_front(string headerName)
    {
        // Accepted, it would be dropped from every request without a word.
        Assert.Throws<ArgumentException>("headerName", () => new ApiKeyCredentials("k-1", headerName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("k-1\r\nX-Injected: yes")]
    [InlineData("k-1\n")]
    public void A_key_that_could_not_be_sent_is_refused_up_front(string apiKey)
    {
        Assert.Throws<ArgumentException>("apiKey", () => new ApiKeyCredentials(apiKey));
    }

    [Fact]
    public async Task A_new_key_and_header_name_replace_the_old_pair_together()
    {
        var provider = new ApiKeyAuthProvider(new ApiKeyCredentials("old", "X-Old"));
        var outcomes = Outcomes(provider);

        provider.UpdateCredentials(new ApiKeyCredentials("new", "X-New"));

        var request = await ApplyAsync(provider);
        Assert.Equal("new", Header(request, "X-New"));
        Assert.Null(Header(request, "X-Old"));
        Assert.True(Assert.Single(outcomes).IsSuccess);
    }

    [Fact]
    public async Task A_missing_api_key_replacement_is_refused_and_the_old_one_kept()
    {
        var provider = new ApiKeyAuthProvider(new ApiKeyCredentials("old"));
        var outcomes = Outcomes(provider);

        provider.UpdateCredentials(null!);

        var outcome = Assert.Single(outcomes);
        Assert.False(outcome.IsSuccess);
        Assert.IsType<ArgumentNullException>(outcome.Error);
        Assert.Equal("old", Header(await ApplyAsync(provider), "X-API-Key"));
    }

    [Fact]
    public void An_api_key_provider_needs_credentials()
    {
        Assert.Throws<ArgumentNullException>("credentials", () => new ApiKeyAuthProvider(null!));
    }

    // ---- Bearer --------------------------------------------------------------------------------

    [Fact]
    public async Task A_bearer_token_goes_in_authorization_with_its_scheme()
    {
        var request = await ApplyAsync(new BearerTokenAuthProvider("t-1"));

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("t-1", request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData("Bearer t-1")]   // the header value instead of the token: the usual mistake
    [InlineData("bearer t-1")]
    [InlineData("")]
    public void A_token_given_with_its_scheme_or_empty_is_refused(string token)
    {
        Assert.Throws<ArgumentException>("bearerToken", () => new BearerTokenAuthProvider(token));
    }

    [Fact]
    public async Task A_token_that_merely_contains_the_word_is_accepted()
    {
        // Opaque tokens are arbitrary text; only the scheme as a prefix gives the mistake away.
        var request = await ApplyAsync(new BearerTokenAuthProvider("abcBearerxyz"));

        Assert.Equal("abcBearerxyz", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A_refused_token_replacement_keeps_the_working_token()
    {
        var provider = new BearerTokenAuthProvider("good");
        var outcomes = Outcomes(provider);

        provider.UpdateCredentials("Bearer oops");
        provider.UpdateCredentials("better");

        Assert.Collection(outcomes,
            refused => Assert.IsType<ArgumentException>(refused.Error),
            accepted => Assert.True(accepted.IsSuccess));

        Assert.Equal("better", (await ApplyAsync(provider)).Headers.Authorization!.Parameter);
    }

    // ---- Basic ---------------------------------------------------------------------------------

    [Fact]
    public void A_colon_in_the_login_is_refused_but_not_in_the_password()
    {
        // RFC 7617: the server splits at the first colon, so only the user name may not have one.
        Assert.Throws<ArgumentException>("login", () => new BasicAuthCredentials("svc:x", "secret"));

        var credentials = new BasicAuthCredentials("svc", "p:ss");
        Assert.Equal("p:ss", credentials.Password);
    }

    [Fact]
    public async Task Basic_credentials_are_encoded_as_the_rfc_says()
    {
        var request = await ApplyAsync(new HttpBasicAuthProvider(new BasicAuthCredentials("svc", "p:ss")));

        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("svc:p:ss", Encoding.GetEncoding("ISO-8859-1").GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public void A_basic_provider_needs_credentials()
    {
        Assert.Throws<ArgumentNullException>("credentials", () => new HttpBasicAuthProvider(null!));
    }

    [Fact]
    public async Task A_missing_basic_replacement_is_refused_and_the_old_one_kept()
    {
        var provider = new HttpBasicAuthProvider(new BasicAuthCredentials("svc", "old"));
        var outcomes = Outcomes(provider);

        provider.UpdateCredentials(null!);

        Assert.IsType<ArgumentNullException>(Assert.Single(outcomes).Error);
        Assert.Equal(
            "svc:old",
            Encoding.GetEncoding("ISO-8859-1").GetString(Convert.FromBase64String((await ApplyAsync(provider)).Headers.Authorization!.Parameter!)));
    }

    // ---- Delegate ------------------------------------------------------------------------------

    [Fact]
    public async Task A_plain_lambda_decorates_the_request()
    {
        var request = await ApplyAsync(new DelegateAuthProvider(r => r.Headers.Add("X-Tenant", "acme")));

        Assert.Equal("acme", Header(request, "X-Tenant"));
    }

    [Fact]
    public async Task An_asynchronous_lambda_is_awaited_and_gets_the_token()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        var provider = new DelegateAuthProvider(async (r, ct) =>
        {
            seen = ct;
            await Task.Delay(10, ct);
            r.Headers.Add("X-Late", "yes");
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/cometd");
        await provider.ApplyAsync(request, cts.Token);

        Assert.Equal("yes", Header(request, "X-Late"));
        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task Several_providers_combine_in_one_lambda()
    {
        var key = new ApiKeyAuthProvider(new ApiKeyCredentials("k-1"));
        var token = new BearerTokenAuthProvider("t-1");

        var request = await ApplyAsync(new DelegateAuthProvider(async (r, ct) =>
        {
            await key.ApplyAsync(r, ct);
            await token.ApplyAsync(r, ct);
        }));

        Assert.Equal("k-1", Header(request, "X-API-Key"));
        Assert.Equal("t-1", request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task What_the_lambda_throws_reaches_the_request()
    {
        var provider = new DelegateAuthProvider(_ => throw new InvalidOperationException("no tenant"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync(provider));
    }

    [Fact]
    public void A_delegate_provider_needs_a_lambda()
    {
        Assert.Throws<ArgumentNullException>("apply", () => new DelegateAuthProvider((Action<HttpRequestMessage>)null!));
        Assert.Throws<ArgumentNullException>("apply", () => new DelegateAuthProvider((Func<HttpRequestMessage, CancellationToken, Task>)null!));
    }

    // ---- On the wire ---------------------------------------------------------------------------

    [Theory]
    [InlineData(BayeuxTransportType.LongPolling)]
    [InlineData(BayeuxTransportType.WebSocket)]
    public async Task A_replaced_token_reaches_the_server(BayeuxTransportType transport)
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var auth = new BearerTokenAuthProvider("first");

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { ["/topic/auth"] = (_, _) => Task.CompletedTask },
            "cometd",
            transportType: transport);

        await using (var client = new BayeuxClient(http, options, auth, (_, _) => { }))
            await client.ConnectAsync();

        auth.UpdateCredentials("second");

        // Over long-polling the next request carries it; over a WebSocket, the next socket - the
        // client connected again opens one.
        await using (var client = new BayeuxClient(http, options, auth, (_, _) => { }))
            await client.ConnectAsync();

        var sent = server.AuthHeadersSnapshot;
        Assert.Equal("Bearer first", sent.First());
        Assert.Equal("Bearer second", sent.Last());
    }
}
