// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// Reconnection after an error: <see cref="ReconnectOptions"/> on its own, and the client using it.
/// </summary>
/// <remarks>
/// Part of <see cref="BayeuxClientTests"/> to share its helpers. A held <c>/meta/connect</c> on the
/// fake server returns within about a second, so a failure scripted after <c>ConnectAsync</c> is
/// met by the next poll without anything else to trigger it.
/// </remarks>
public partial class BayeuxClientTests
{
    // Short enough to keep the suite fast, long enough to be a real wait.
    private static ReconnectOptions QuickReconnect(
        int? maxAttempts = null,
        Func<Exception, bool>? shouldRetry = null,
        Func<CancellationToken, Task>? beforeAttemptAsync = null) =>
        new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20),
            maxAttempts: maxAttempts, shouldRetry: shouldRetry, beforeAttemptAsync: beforeAttemptAsync);

    private static (BayeuxClientOptions options, ConcurrentQueue<JsonElement> received) ReconnectingOptions(
        string channel, ReconnectOptions? reconnect)
    {
        var received = new ConcurrentQueue<JsonElement>();
        var channels = new Dictionary<string, BayeuxEventHandler> { [channel] = On(data => received.Enqueue(data)) };

        return (new BayeuxClientOptions(channels, "cometd", reconnectOptions: reconnect), received);
    }

    private static TaskCompletionSource<BayeuxDisconnectedEventArgs> NewStopSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- the client with and without a policy ------------------------------------------------

    [Fact]
    public async Task Without_a_reconnect_policy_a_failed_connect_stops_the_client_as_fatal()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", reconnect: null);

        var stopped = NewStopSignal();
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        server.FailNextConnectsWithStatus = 1;

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.Failed, outcome.Reason);
        Assert.Equal(HttpStatusCode.InternalServerError, Assert.IsType<BayeuxHttpException>(outcome.Error).StatusCode);
        Assert.Contains(errors, e => e.Source == ErrorSource.Http && e.IsFatal);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task With_a_reconnect_policy_a_transport_failure_keeps_the_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = ReconnectingOptions("/topic/r", QuickReconnect());

        var raised = false;
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        var firstClientId = client.ClientId;
        server.FailNextConnectsWithStatus = 1;

        Assert.True(await WaitForAsync(() => server.FailNextConnectsWithStatus == 0));

        // A 500 says nothing about the session, which the server still holds with everything
        // queued in it: the connect is retried with the same id, no new handshake, no resubscribe.
        server.Publish("/topic/r", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(1, server.HandshakeCount);
        Assert.Equal(firstClientId, client.ClientId);
        Assert.Single(server.SubscriptionsSnapshot, c => c == "/topic/r");

        // Reported, but not as the end: the client carried on, and no disconnect event came.
        Assert.False(raised);
        Assert.Contains(errors, e => e.Source == ErrorSource.Http && !e.IsFatal);
        Assert.DoesNotContain(errors, e => e.IsFatal);
    }

    [Fact]
    public async Task A_status_the_server_chose_is_not_retried_by_default()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect());

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        // Repeating a rejected login is how a service account gets locked.
        server.ConnectFailureStatus = 401;
        server.FailNextConnectsWithStatus = 1;

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.Failed, outcome.Reason);
        Assert.Equal(HttpStatusCode.Unauthorized, Assert.IsType<BayeuxHttpException>(outcome.Error).StatusCode);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task A_custom_predicate_retries_what_the_default_refuses_and_refreshes_first()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        // The Salesforce pattern: 401 means an expired token, worth retrying because a fresh one
        // is fetched before every attempt.
        var handshakesBeforeEachAttempt = new ConcurrentQueue<int>();

        var reconnect = QuickReconnect(
            shouldRetry: e => e is BayeuxHttpException { StatusCode: HttpStatusCode.Unauthorized }
                           || ReconnectOptions.IsRetriable(e),
            beforeAttemptAsync: _ =>
            {
                handshakesBeforeEachAttempt.Enqueue(server.HandshakeCount);
                return Task.CompletedTask;
            });

        var (options, _) = ReconnectingOptions("/topic/r", reconnect);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        server.ConnectFailureStatus = 401;
        server.FailNextConnectsWithStatus = 1;

        Assert.True(await WaitForAsync(() => !handshakesBeforeEachAttempt.IsEmpty));
        Assert.True(await WaitForAsync(() => server.FailNextConnectsWithStatus == 0));

        // Retried rather than ending the client - and in the same session, since an HTTP status is
        // a transport failure. The hook still ran first: credentials can expire whether or not
        // the session survived. Once, and not before ConnectAsync's own handshake.
        Assert.Equal(new[] { 1 }, handshakesBeforeEachAttempt.ToArray());
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task The_hook_runs_before_a_new_handshake_too()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var handshakesBeforeEachAttempt = new ConcurrentQueue<int>();

        var reconnect = QuickReconnect(beforeAttemptAsync: _ =>
        {
            handshakesBeforeEachAttempt.Enqueue(server.HandshakeCount);
            return Task.CompletedTask;
        });

        var (options, _) = ReconnectingOptions("/topic/r", reconnect);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();

        // The server invalidates the session: a new handshake, with fresh credentials first.
        server.FailNextConnectsWith402 = 1;

        Assert.True(await WaitForAsync(() => server.HandshakeCount == 2));
        Assert.Equal(new[] { 1 }, handshakesBeforeEachAttempt.ToArray());
    }

    [Fact]
    public async Task Running_out_of_attempts_stops_the_client_with_the_last_error()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect(maxAttempts: 3));

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        // A refusal the default policy does retry, so only the count can end it. The 402 sends the
        // client to a new handshake: a transport failure would keep the session and never reach one.
        server.RejectedHandshakeReconnect = "handshake";
        server.RejectNextHandshakes = int.MaxValue;
        server.FailNextConnectsWith402 = 1;

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.Failed, outcome.Reason);
        Assert.Equal("handshake", Assert.IsType<BayeuxHandshakeException>(outcome.Error).Reconnect);

        // The first handshake, the one the 402 asked for - not an attempt, since nothing had failed
        // yet - and one per retry after its refusal.
        Assert.Equal(1 + 1 + 3, server.HandshakeCount);
    }

    [Fact]
    public async Task A_402_between_failures_does_not_start_the_count_again()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect(maxAttempts: 2));

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        // A server that accepts every handshake and fails every session soon after. A 402 proves
        // nothing about the new session, so it must not count as the success that resets the
        // attempts:
        //   500 -> attempt 1, same session;  402 -> new session, still 1;
        //   500 -> attempt 2, same session;  500 -> out of attempts.
        // Were the 402 to reset the count, the third failure would be attempt 1 again, the fourth
        // attempt 2, and the client would carry on on the unscripted connects that follow.
        server.ScriptConnects(500, 402, 500, 500);

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.Failed, outcome.Reason);
        Assert.Equal(HttpStatusCode.InternalServerError, Assert.IsType<BayeuxHttpException>(outcome.Error).StatusCode);

        // The first handshake and the one the 402 asked for. The 500s kept the session.
        Assert.Equal(2, server.HandshakeCount);
    }

    [Fact]
    public async Task A_handshake_the_server_advises_against_repeating_is_not_retried()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect());

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        server.RejectNextHandshakes = 1;                 // with advice "none", the default
        server.FailNextConnectsWith402 = 1;              // the server drops the session: a new handshake

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        var refused = Assert.IsType<BayeuxHandshakeException>(outcome.Error);
        Assert.Equal("none", refused.Reconnect);
        Assert.Equal("403::Handshake denied", refused.Error);
        Assert.Equal(2, server.HandshakeCount);
    }

    // What Salesforce sends for a revoked token: a generic error on the message, the real cause in ext.
    private const string SalesforceRevokedExt = """{"sfdc":{"failureReason":"401::Authentication invalid"}}""";

    [Fact]
    public async Task A_refused_connect_stops_the_client_with_the_servers_reason()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", reconnect: null);

        var stopped = NewStopSignal();
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        server.RefuseNextConnect("401::Authentication invalid", SalesforceRevokedExt);

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        // A refusal, not a clean stop: the reason reaches the caller instead of vanishing into
        // ServerRequirement.
        Assert.Equal(DisconnectReason.Failed, outcome.Reason);

        var refused = Assert.IsType<BayeuxConnectException>(outcome.Error);
        Assert.Equal("401::Authentication invalid", refused.Error);
        Assert.Equal("none", refused.Reconnect);
        Assert.Equal("401::Authentication invalid", refused.Ext!["sfdc"].GetProperty("failureReason").GetString());

        // Reported once, as fatal, where it was created.
        Assert.Single(errors, e => e.Error == refused && e.Source == ErrorSource.Connect && e.IsFatal);

        // The server has already dropped the session: nothing is sent to close it.
        Assert.Equal(0, server.DisconnectCount);
        Assert.Equal(string.Empty, client.ClientId);
    }

    [Fact]
    public async Task A_refused_connect_is_not_retried_by_default()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect());

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        // Advice "none": the server says the same again is pointless, and the default agrees.
        server.RefuseNextConnect("401::Authentication invalid");

        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.Failed, outcome.Reason);
        Assert.IsType<BayeuxConnectException>(outcome.Error);
        Assert.Equal(1, server.HandshakeCount);
    }

    [Fact]
    public async Task A_client_that_re_authenticates_may_retry_a_revoked_token()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        // The Salesforce pattern, the way Salesforce actually reports it: not an HTTP 401 but a
        // Bayeux refusal on /meta/connect. Worth retrying only because a fresh token is fetched first.
        var refreshes = 0;

        var reconnect = QuickReconnect(
            shouldRetry: e => e is BayeuxConnectException { Error: "401::Authentication invalid" }
                           || ReconnectOptions.IsRetriable(e),
            beforeAttemptAsync: _ =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.CompletedTask;
            });

        var (options, received) = ReconnectingOptions("/topic/r", reconnect);
        var raised = false;

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        await client.ConnectAsync();

        server.RefuseNextConnect("401::Authentication invalid", SalesforceRevokedExt);

        Assert.True(await WaitForAsync(() => server.HandshakeCount == 2));
        Assert.True(await WaitForAsync(() => client.ClientId.Length > 0));

        server.Publish("/topic/r", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));

        Assert.Equal(1, Volatile.Read(ref refreshes));
        Assert.False(raised);
    }

    [Fact]
    public async Task A_refused_handshake_carries_the_servers_ext()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", reconnect: null);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        // Salesforce's handshake refusal says only "403::Handshake denied"; a revoked token, a
        // connection limit and a busy server look the same until ext is read.
        server.RejectNextHandshakes = 1;
        server.RejectedHandshakeExtJson = SalesforceRevokedExt;

        var refused = await Assert.ThrowsAsync<BayeuxHandshakeException>(() => client.ConnectAsync());

        Assert.Equal("403::Handshake denied", refused.Error);
        Assert.Equal("401::Authentication invalid", refused.Ext!["sfdc"].GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task ConnectAsync_is_never_retried_even_with_a_policy()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/r", QuickReconnect());

        var raised = false;
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => raised = true);
        client.OnError += (_, e) => errors.Enqueue(e);

        // Retriable by the policy's rules, yet the caller hears about it at once.
        server.RejectedHandshakeReconnect = "handshake";
        server.RejectNextHandshakes = 1;

        var refused = await Assert.ThrowsAsync<BayeuxHandshakeException>(() => client.ConnectAsync());

        Assert.Equal("403::Handshake denied", refused.Error);
        Assert.Equal(1, server.HandshakeCount);
        Assert.False(raised);
        Assert.Contains(errors, e => e.Source == ErrorSource.Handshake && e.IsFatal);
    }

    [Fact]
    public async Task A_stop_during_a_delay_after_a_refusal_returns_at_once_and_sends_nothing()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        // Long enough that the test fails on its timeout if the stop does not interrupt the delay.
        // Every error retried, so the refusal below leads to a delay rather than a stop.
        var (options, _) = ReconnectingOptions("/topic/r",
            new ReconnectOptions(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), shouldRetry: _ => true));

        var stopped = NewStopSignal();

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        await client.ConnectAsync();

        // A refusal: the server has dropped the session.
        server.RefuseNextConnect("403::Forbidden");

        // The id is cleared just before the delay starts.
        Assert.True(await WaitForAsync(() => client.ClientId.Length == 0));

        // No session to subscribe on while it is being re-established: an immediate, clear error
        // rather than a request with the id of the lost one.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeNewChannelsAsync(
            new Dictionary<string, BayeuxEventHandler> { ["/topic/other"] = Ignore }));

        var watch = Stopwatch.StartNew();
        await WithTimeoutAsync(client.DisconnectAsync(), TimeSpan.FromSeconds(10));
        watch.Stop();

        // Well below the seven-second disconnect timeout a request to a dead server could cost.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"DisconnectAsync took {watch.Elapsed}.");
        Assert.Equal(DisconnectReason.TokenCancellation, (await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10))).Reason);

        Assert.Equal(0, server.DisconnectCount);     // no goodbye for a session already lost
        Assert.Equal(1, server.HandshakeCount);      // and no attempt after the stop
    }

    [Fact]
    public async Task A_stop_during_a_delay_after_a_transport_failure_closes_the_session()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();

        var (options, _) = ReconnectingOptions("/topic/r", new ReconnectOptions(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)));

        var stopped = NewStopSignal();
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));
        client.OnError += (_, e) => { if (e.Source == ErrorSource.Http) failed.TrySetResult(true); };
        await client.ConnectAsync();

        var clientId = client.ClientId;
        server.FailNextConnectsWithStatus = 1;

        // Reported just before the delay starts.
        await WithTimeoutAsync(failed.Task, TimeSpan.FromSeconds(10));

        // The session may well be alive - the server holds it until maxInterval - so it is kept,
        // and usable meanwhile.
        Assert.Equal(clientId, client.ClientId);
        await client.SubscribeNewChannelsAsync(new Dictionary<string, BayeuxEventHandler> { ["/topic/other"] = Ignore });

        await WithTimeoutAsync(client.DisconnectAsync(), TimeSpan.FromSeconds(10));

        Assert.Equal(DisconnectReason.TokenCancellation, (await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10))).Reason);

        Assert.Equal(1, server.DisconnectCount);     // closed like any live session
        Assert.Equal(1, server.HandshakeCount);      // and no attempt after the stop
    }

    // ---- ReconnectOptions on its own -----------------------------------------------------------

    [Fact]
    public void ReconnectOptions_has_safe_defaults()
    {
        var options = new ReconnectOptions();

        Assert.Equal(TimeSpan.FromSeconds(1), options.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxDelay);
        Assert.Equal(2, options.Multiplier);
        Assert.Null(options.MaxAttempts);
        Assert.Null(options.BeforeAttemptAsync);

        // The default predicate is IsRetriable.
        Assert.True(options.ShouldRetry(new HttpRequestException("connection refused")));
        Assert.False(options.ShouldRetry(new BayeuxHttpException(HttpStatusCode.Unauthorized, null)));

        // A long first delay raises the default cap with it instead of failing the range check.
        Assert.Equal(TimeSpan.FromMinutes(2), new ReconnectOptions(initialDelay: TimeSpan.FromMinutes(2)).MaxDelay);
    }

    [Fact]
    public void ReconnectOptions_rejects_values_outside_their_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>("initialDelay", () => new ReconnectOptions(initialDelay: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>("maxDelay", () => new ReconnectOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>("multiplier", () => new ReconnectOptions(multiplier: 0.5));
        Assert.Throws<ArgumentOutOfRangeException>("multiplier", () => new ReconnectOptions(multiplier: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>("maxAttempts", () => new ReconnectOptions(maxAttempts: 0));

        // The boundaries themselves are allowed: a constant delay, a single attempt.
        _ = new ReconnectOptions(multiplier: 1, maxAttempts: 1);
    }

    [Fact]
    public void The_delay_bound_grows_by_the_multiplier_up_to_the_cap()
    {
        var options = new ReconnectOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), multiplier: 2);

        var bounds = Enumerable.Range(1, 6).Select(attempt => options.GetReconnectDelay(attempt, 1.0));

        Assert.Equal(new[] { 1, 2, 4, 8, 10, 10 }.Select(s => TimeSpan.FromSeconds(s)), bounds);

        // Full jitter: the random factor places the delay anywhere between zero and the bound.
        Assert.Equal(TimeSpan.Zero, options.GetReconnectDelay(3, 0.0));
        Assert.Equal(TimeSpan.FromSeconds(2), options.GetReconnectDelay(3, 0.5));

        // Far past the cap Math.Pow overflows to infinity; the delay stays at the cap.
        Assert.Equal(TimeSpan.FromSeconds(10), options.GetReconnectDelay(5000, 1.0));
    }

    [Fact]
    public void Attempts_are_unlimited_unless_a_maximum_is_set()
    {
        Assert.True(new ReconnectOptions().CanRetry(1_000_000));

        var limited = new ReconnectOptions(maxAttempts: 2);

        Assert.True(limited.CanRetry(0));
        Assert.True(limited.CanRetry(1));
        Assert.False(limited.CanRetry(2));
    }

    public static TheoryData<Exception, bool> RetriableCases => new()
    {
        { new HttpRequestException("connection refused"), true },
        { new TaskCanceledException("HttpClient.Timeout"), true },
        { new BayeuxHttpException(HttpStatusCode.InternalServerError, null), true },
        { new BayeuxHttpException(HttpStatusCode.ServiceUnavailable, null), true },
        { new BayeuxHttpException((HttpStatusCode)429, null), true },
        { new BayeuxHttpException(HttpStatusCode.RequestTimeout, null), true },
        { new BayeuxHttpException(HttpStatusCode.Unauthorized, null), false },
        { new BayeuxHttpException(HttpStatusCode.Forbidden, null), false },
        { new BayeuxHttpException(HttpStatusCode.NotFound, null), false },
        { new BayeuxHandshakeException("403::Handshake denied", "none"), false },
        { new BayeuxHandshakeException("403::Handshake denied", null), false },
        { new BayeuxHandshakeException("402::Unknown client", "handshake"), true },
        { new BayeuxHandshakeException(null, "retry"), true },
        { new BayeuxConnectException("401::Authentication invalid", "none"), false },
        { new BayeuxConnectException("403::Unknown client", "handshake"), true },
        { new InvalidOperationException("malformed reply"), false },
    };

    [Theory]
    [MemberData(nameof(RetriableCases))]
    public void IsRetriable_retries_only_what_can_clear_by_itself(Exception error, bool expected) =>
        Assert.Equal(expected, ReconnectOptions.IsRetriable(error));

    [Fact]
    public void BayeuxHttpException_carries_its_status_on_every_target()
    {
        var error = new BayeuxHttpException(HttpStatusCode.BadGateway, "Bad Gateway");

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Contains("502 (Bad Gateway)", error.Message);

        // Code that already catches HttpRequestException keeps catching it.
        Assert.IsAssignableFrom<HttpRequestException>(error);

#if NET5_0_OR_GREATER
        // And code that reads the status from the base class finds it there too.
        Assert.Equal(HttpStatusCode.BadGateway, ((HttpRequestException)error).StatusCode);
#endif
    }
}
