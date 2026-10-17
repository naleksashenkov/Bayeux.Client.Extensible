// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// <see cref="RefreshingTokenAuthProvider"/>: one fetch however many requests ask at once, a
/// refresh ahead of expiry that a failure does not turn into an outage, and no stampede for a
/// token that lives only seconds.
/// </summary>
public class RefreshingTokenTests
{
    // A token source that counts its calls and hands out t-1, t-2, ... valid for `lifetime`.
    private sealed class TokenSource
    {
        private int _calls;

        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

        public TimeSpan Delay { get; set; }

        public Exception? FailWith { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public async Task<AccessToken> GetAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            if (FailWith is { } failure)
                throw failure;

            return new AccessToken("t-" + call, DateTimeOffset.UtcNow + Lifetime);
        }
    }

    private static async Task<string?> SentTokenAsync(IAuthProvider provider, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/cometd");
        await provider.ApplyAsync(request, cancellationToken);

        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        return request.Headers.Authorization.Parameter;
    }

    [Fact]
    public async Task The_first_request_fetches_a_token_and_the_next_reuse_it()
    {
        var source = new TokenSource();
        var provider = new RefreshingTokenAuthProvider(source.GetAsync);

        Assert.Equal("t-1", await SentTokenAsync(provider));
        Assert.Equal("t-1", await SentTokenAsync(provider));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Requests_that_need_a_token_at_once_share_one_fetch()
    {
        // A connect, a publish and a subscribe all finding the token due: one of them fetches,
        // the others wait for it and take its result - not twenty calls to the token server.
        var source = new TokenSource { Delay = TimeSpan.FromMilliseconds(200) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync);

        var sent = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => SentTokenAsync(provider))));

        Assert.Equal(1, source.Calls);
        Assert.All(sent, token => Assert.Equal("t-1", token));
    }

    [Fact]
    public async Task A_token_is_refreshed_before_it_expires()
    {
        // Lives 2 s; refreshed a second ahead - the default margin, capped at half the lifetime.
        var source = new TokenSource { Lifetime = TimeSpan.FromSeconds(2) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync, TimeSpan.FromSeconds(1));

        Assert.Equal("t-1", await SentTokenAsync(provider));

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // Not yet expired, but due: the request already goes out with a new one.
        Assert.Equal("t-2", await SentTokenAsync(provider));
    }

    [Fact]
    public async Task A_failed_refresh_of_a_token_still_valid_sends_the_old_one_and_reports_it()
    {
        var source = new TokenSource { Lifetime = TimeSpan.FromSeconds(2) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync, TimeSpan.FromSeconds(1));

        var outcomes = new ConcurrentQueue<OnCredentialsUpdatedEventArgs>();
        provider.OnCredentialsUpdated += (_, e) => outcomes.Enqueue(e);

        Assert.Equal("t-1", await SentTokenAsync(provider));
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        // The token server is down; the token in hand is good for most of a second more.
        source.FailWith = new HttpRequestException("token server unreachable");

        Assert.Equal("t-1", await SentTokenAsync(provider));

        Assert.Collection(outcomes,
            first => Assert.True(first.IsSuccess),
            failed => Assert.IsType<HttpRequestException>(failed.Error));
    }

    [Fact]
    public async Task Without_a_valid_token_a_failed_fetch_fails_the_request()
    {
        var source = new TokenSource { FailWith = new HttpRequestException("token server unreachable") };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync);

        // Nothing to send instead: the request fails, and the client's reconnect policy decides.
        await Assert.ThrowsAsync<HttpRequestException>(() => SentTokenAsync(provider));

        source.FailWith = null;
        Assert.Equal("t-2", await SentTokenAsync(provider));
    }

    [Fact]
    public async Task An_expired_token_is_never_sent()
    {
        var source = new TokenSource { Lifetime = TimeSpan.FromMilliseconds(600) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync, TimeSpan.Zero);

        Assert.Equal("t-1", await SentTokenAsync(provider));
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        source.FailWith = new HttpRequestException("token server unreachable");

        await Assert.ThrowsAsync<HttpRequestException>(() => SentTokenAsync(provider));
    }

    [Fact]
    public async Task Invalidate_makes_the_next_request_fetch_a_new_token()
    {
        // The server revoked the token before its time - Salesforce's 401::Authentication invalid.
        var source = new TokenSource();
        var provider = new RefreshingTokenAuthProvider(source.GetAsync);

        Assert.Equal("t-1", await SentTokenAsync(provider));

        provider.Invalidate();

        Assert.Equal("t-2", await SentTokenAsync(provider));
    }

    [Fact]
    public async Task A_short_lived_token_does_not_stampede_the_token_server()
    {
        // 4 s of life against a minute's margin: refreshed at half-life, not from the first second.
        var source = new TokenSource { Lifetime = TimeSpan.FromSeconds(4) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 10; i++)
            Assert.Equal("t-1", await SentTokenAsync(provider));

        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_fail_the_others()
    {
        var source = new TokenSource { Delay = TimeSpan.FromMilliseconds(300) };
        var provider = new RefreshingTokenAuthProvider(source.GetAsync);

        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var gaveUp = SentTokenAsync(provider, impatient.Token);
        var waited = SentTokenAsync(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        Assert.StartsWith("t-", await waited);
    }

    [Fact]
    public async Task A_token_source_that_returns_nothing_or_a_dead_token_fails_clearly()
    {
        var nothing = new RefreshingTokenAuthProvider(_ => Task.FromResult<AccessToken>(null!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SentTokenAsync(nothing));

        var expired = new RefreshingTokenAuthProvider(_ => Task.FromResult(new AccessToken("t", DateTimeOffset.UtcNow.AddMinutes(-1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SentTokenAsync(expired));
    }

    [Fact]
    public void The_provider_checks_its_arguments()
    {
        Assert.Throws<ArgumentNullException>("getFreshToken", () => new RefreshingTokenAuthProvider(null!));
        Assert.Throws<ArgumentOutOfRangeException>("refreshBefore",
            () => new RefreshingTokenAuthProvider(_ => Task.FromResult(new AccessToken("t", DateTimeOffset.UtcNow.AddHours(1))), TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer t-1")]
    public void An_access_token_that_could_not_be_sent_is_refused(string token)
    {
        Assert.Throws<ArgumentException>("token", () => new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task A_refreshed_token_reaches_the_server()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var source = new TokenSource();
        var auth = new RefreshingTokenAuthProvider(source.GetAsync);

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler> { ["/topic/auth"] = (_, _) => Task.CompletedTask }, "cometd");

        await using var client = new BayeuxClient(http, options, auth, (_, _) => { });
        await client.ConnectAsync();

        auth.Invalidate();
        await client.ConnectAsync();

        var sent = server.AuthHeadersSnapshot;
        Assert.Equal("Bearer t-1", sent.First());
        Assert.Equal("Bearer t-2", sent.Last());
    }
}
