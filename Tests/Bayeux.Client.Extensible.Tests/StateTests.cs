// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Xunit;

namespace Bayeux.Client.Extensible.Tests;

/// <summary><see cref="IBayeuxClient.State"/> and <see cref="IBayeuxClient.OnStateChanged"/>.</summary>
/// <remarks>Part of <see cref="BayeuxClientTests"/> to share its helpers.</remarks>
public partial class BayeuxClientTests
{
    // A class rather than a positional record: records need init accessors, which net472 lacks.
    private sealed class Transition
    {
        public Transition(BayeuxClientState from, BayeuxClientState to, Exception? error)
        {
            From = from;
            To = to;
            Error = error;
        }

        public BayeuxClientState From { get; }

        public BayeuxClientState To { get; }

        public Exception? Error { get; }
    }

    // Every change, in order. One place that reads the event's properties.
    private static ConcurrentQueue<Transition> RecordStates(IBayeuxClient client)
    {
        var transitions = new ConcurrentQueue<Transition>();
        client.OnStateChanged += (_, e) => transitions.Enqueue(new Transition(e.PreviousState, e.CurrentState, e.Error));
        return transitions;
    }

    private static (BayeuxClientState, BayeuxClientState)[] Steps(ConcurrentQueue<Transition> transitions) =>
        transitions.Select(t => (t.From, t.To)).ToArray();

    [Fact]
    public async Task A_session_goes_from_disconnected_to_connected_and_back()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        var transitions = RecordStates(client);

        Assert.Equal(BayeuxClientState.Disconnected, client.State);

        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        await client.DisconnectAsync();
        Assert.Equal(BayeuxClientState.Disconnected, client.State);

        Assert.Equal(
            new[]
            {
                (BayeuxClientState.Disconnected, BayeuxClientState.Connecting),
                (BayeuxClientState.Connecting, BayeuxClientState.Connected),
                (BayeuxClientState.Connected, BayeuxClientState.Disconnected)
            },
            Steps(transitions));

        Assert.All(transitions, t => Assert.Null(t.Error));
    }

    [Fact]
    public async Task Connected_waits_for_a_poll_the_server_answered()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });

        await client.ConnectAsync();

        // The fake server holds the first connect for about a second. ConnectAsync has returned,
        // but nothing has proved polling works yet: still Connecting.
        Assert.Equal(BayeuxClientState.Connecting, client.State);
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));
    }

    [Fact]
    public async Task Connected_is_reported_once_however_many_polls_succeed()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        var transitions = RecordStates(client);
        await client.ConnectAsync();

        // Each event ends a poll, and every successful poll sets Connected again.
        for (var n = 1; n <= 3; n++)
        {
            server.Publish("/topic/s", new { n });
            Assert.True(await WaitForAsync(() => received.Count == n));
        }

        Assert.Single(transitions, t => t.To == BayeuxClientState.Connected);
    }

    [Fact]
    public async Task A_failed_connect_returns_to_disconnected_with_its_error()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        var transitions = RecordStates(client);

        server.RejectNextHandshakes = 1;
        var refused = await Assert.ThrowsAsync<BayeuxHandshakeException>(() => client.ConnectAsync());

        Assert.Equal(BayeuxClientState.Disconnected, client.State);
        Assert.Equal(
            new[] { (BayeuxClientState.Disconnected, BayeuxClientState.Connecting), (BayeuxClientState.Connecting, BayeuxClientState.Disconnected) },
            Steps(transitions));
        Assert.Same(refused, transitions.Last().Error);
    }

    [Fact]
    public async Task A_new_handshake_the_server_asks_for_is_reconnecting()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        var transitions = RecordStates(client);
        server.FailNextConnectsWith402 = 1;

        Assert.True(await WaitForAsync(() => transitions.Count == 2));

        // No policy needed: the protocol's own recovery. Not an error either.
        Assert.Equal(
            new[] { (BayeuxClientState.Connected, BayeuxClientState.Reconnecting), (BayeuxClientState.Reconnecting, BayeuxClientState.Connected) },
            Steps(transitions));
        Assert.All(transitions, t => Assert.Null(t.Error));
    }

    [Fact]
    public async Task A_retried_failure_is_reconnecting_with_its_error()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = ReconnectingOptions("/topic/s", QuickReconnect());

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        var transitions = RecordStates(client);
        server.FailNextConnectsWithStatus = 1;

        Assert.True(await WaitForAsync(() => transitions.Count == 2));

        var interrupted = transitions.First();
        Assert.Equal((BayeuxClientState.Connected, BayeuxClientState.Reconnecting), (interrupted.From, interrupted.To));
        Assert.IsType<BayeuxHttpException>(interrupted.Error);

        Assert.Equal((BayeuxClientState.Reconnecting, BayeuxClientState.Connected), (transitions.Last().From, transitions.Last().To));
    }

    [Fact]
    public async Task Disconnected_comes_before_the_disconnect_event()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        BayeuxClientState? seenByTheEvent = null;
        var stopped = NewStopSignal();
        BayeuxClient? client = null;

        await using var created = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) =>
        {
            seenByTheEvent = client!.State;
            stopped.TrySetResult(e);
        });
        client = created;
        var transitions = RecordStates(client);

        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        // No policy: a failure ends the client.
        server.FailNextConnectsWithStatus = 1;
        var outcome = await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10));

        // A handler that reconnects from the event must not have its Connecting overwritten by a
        // late Disconnected - so the state changes first.
        Assert.Equal(BayeuxClientState.Disconnected, seenByTheEvent);
        Assert.Same(outcome.Error, transitions.Last().Error);
    }

    [Fact]
    public async Task Reconnecting_from_the_disconnect_event_ends_connected()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        BayeuxClient? client = null;
        var reconnected = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var created = new BayeuxClient(http, options, NoAuthProvider.Instance, async (_, _) =>
        {
            try
            {
                await client!.ConnectAsync();
                reconnected.TrySetResult(null);
            }
            catch (Exception ex)
            {
                reconnected.TrySetResult(ex);
            }
        });
        client = created;

        await client.ConnectAsync();
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        server.StopOnNextConnect = true;

        Assert.Null(await WithTimeoutAsync(reconnected.Task, TimeSpan.FromSeconds(10)));
        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));
        Assert.Equal(2, server.HandshakeCount);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_the_client()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, received) = Options("/topic/s");

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { });
        client.OnStateChanged += (_, _) => throw new InvalidOperationException("subscriber bug");
        await client.ConnectAsync();

        Assert.True(await WaitForAsync(() => client.State == BayeuxClientState.Connected));

        server.Publish("/topic/s", new { n = 1 });
        Assert.True(await WaitForAsync(() => received.Count == 1));
    }

    [Fact]
    public async Task A_subscriber_follows_the_rules_of_a_handler()
    {
        using var server = new FakeBayeuxServer();
        using var http = server.CreateClient();
        var (options, _) = Options("/topic/s");

        Exception? connectFromSubscriber = null;
        var stopped = NewStopSignal();

        // Plain "using": if DisconnectAsync from the subscriber did wait for the loop, the loop would
        // be waiting for the subscriber, and DisposeAsync would then hang the run instead of failing.
        using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, e) => stopped.TrySetResult(e));

        client.OnStateChanged += (_, e) =>
        {
            if (e.CurrentState != BayeuxClientState.Connected)
                return;

            // Refused rather than left to hang: restarting would mean waiting for this very loop.
            try { client.ConnectAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { connectFromSubscriber = ex; }

            // Only signals - the blocking wait here returns at once instead of deadlocking.
            client.DisconnectAsync().GetAwaiter().GetResult();
        };

        await client.ConnectAsync();

        Assert.Equal(DisconnectReason.TokenCancellation, (await WithTimeoutAsync(stopped.Task, TimeSpan.FromSeconds(10))).Reason);
        Assert.IsType<InvalidOperationException>(connectFromSubscriber);
        Assert.Equal(BayeuxClientState.Disconnected, client.State);
    }
}
