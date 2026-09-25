// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// Passing a logger, observing errors, and reconnecting &#8212; which the poller never does by
/// itself.
/// </summary>
public static class LoggingAndReconnectExample
{
    public static async Task RunAsync(Uri serverBaseAddress, ILoggerFactory loggerFactory)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/orders"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; };

        var options = new CometdPollerOptions(channels, "cometd");

        // Signals a reconnect is wanted. The handler must not reconnect inline: see below.
        using var reconnectWanted = new SemaphoreSlim(0, 1);

        await using var poller = new CometDPoller(
            http,
            options,
            NoAuthProvider.Instance,
            onPollerDisconnected: (_, e) =>
            {
                // The server asked us to stop: reconnecting would ignore the protocol.
                if (e.Reason == DisconnectReason.ServerRequirement)
                    return;

                // A deliberate DisconnectAsync or Dispose needs no reconnect either.
                if (e.Reason == DisconnectReason.TokenCancellation)
                    return;

                reconnectWanted.Release();
            },
            logger: loggerFactory.CreateLogger<CometDPoller>());

        poller.OnError += (_, e) =>
            Console.WriteLine($"[{e.Source}] {e.Error.Message} (fatal: {e.IsFatal})");

        await poller.ConnectAsync();

        // The poller never retries on its own: one dropped connection ends the session and the
        // decision to come back is yours. Reconnecting from the handler with no delay would retry
        // as fast as the server can refuse, so pace it here instead.
        using var stopping = new CancellationTokenSource();
        var delay = TimeSpan.FromSeconds(1);

        while (!stopping.IsCancellationRequested)
        {
            await reconnectWanted.WaitAsync(stopping.Token);

            try
            {
                await Task.Delay(delay, stopping.Token);
                await poller.ConnectAsync();

                delay = TimeSpan.FromSeconds(1);   // recovered, reset the backoff
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"reconnect failed: {ex.Message}");

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(2).Ticks));
                reconnectWanted.Release();         // try again after the longer delay
            }
        }
    }
}
