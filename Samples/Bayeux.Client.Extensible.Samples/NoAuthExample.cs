// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// No authentication of its own. Covers two cases: a server that needs none, and a deployment
/// where an application login established the session and the poller only has to carry its cookie.
/// </summary>
public static class NoAuthExample
{
    /// <summary>The server needs no credentials at all.</summary>
    public static async Task RunOpenServerAsync(Uri serverBaseAddress)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/topic/public"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; };

        await using var poller = new CometDPoller(
            http,
            new CometdPollerOptions(channels, "cometd"),
            NoAuthProvider.Instance,
            onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        await poller.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The session comes from an application login elsewhere. The poller adds no credentials of
    /// its own; it carries the cookie it is given, and captures anything the server sets later
    /// (CometD's <c>BAYEUX_BROWSER</c> among them).
    /// </summary>
    public static async Task RunInheritedSessionAsync(Uri serverBaseAddress, string sessionCookie)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        // Seed the container with whatever the login produced. Give each poller its own
        // container: sharing one makes CometD treat the sessions as a single browser and
        // demote all but one to interval polling.
        var cookies = new CookieContainer();
        cookies.Add(serverBaseAddress, new Cookie("SESSIONID", sessionCookie));

        var channels = new ConcurrentDictionary<string, BayeuxEventHandler>();
        channels["/v2/me/notifications"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; };

        await using var poller = new CometDPoller(
            http,
            new CometdPollerOptions(channels, "cometd"),
            NoAuthProvider.Instance,
            onPollerDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"),
            cookie: cookies);

        await poller.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}
