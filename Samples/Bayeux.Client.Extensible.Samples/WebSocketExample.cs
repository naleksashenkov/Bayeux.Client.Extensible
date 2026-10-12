// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// The same session over a WebSocket instead of long-polling: one option, and the rest of the
/// code does not change. Or <see cref="BayeuxTransportType.Auto"/>, to let the network decide.
/// </summary>
/// <remarks>
/// The server must have WebSocket configured - a CometD server does when its WebSocket transport
/// is on. Salesforce does not offer it.
/// </remarks>
public static class WebSocketExample
{
    public static async Task RunAsync(Uri serverBaseAddress, string login, string password)
    {
        // Only BaseAddress is used for a WebSocket: http becomes ws, https becomes wss. The
        // handler's settings and the timeout apply to long-polling only.
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress
        };

        // Called once per socket, for the upgrade request; its headers are carried over.
        // UpdateCredentials takes effect when the next socket is opened.
        var auth = new HttpBasicAuthProvider(new BasicAuthCredentials(login, password));

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            // Pushed the moment the server has it, and handled in the order it was sent.
            ["/topic/prices"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        var options = new BayeuxClientOptions(
            channels,
            "cometd",
            transportType: BayeuxTransportType.WebSocket);

        await using var client = new BayeuxClient(
            http,
            options,
            auth,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason} {e.Error?.Message}"));

        client.OnStateChanged += (_, e) => Console.WriteLine($"{e.PreviousState} -> {e.CurrentState}");

        // A server without WebSocket refuses the upgrade: ConnectAsync throws WebSocketException.
        // There is no automatic fallback - choose long-polling for such a server.
        await client.ConnectAsync();

        await client.PublishAsync("/topic/prices", new { symbol = "ACME", price = 42.5m });

        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// WebSocket where it gets through, long-polling where it does not: for a client deployed
    /// behind proxies nobody has checked.
    /// </summary>
    public static async Task RunAutoAsync(Uri serverBaseAddress)
    {
        // Both transports' rules apply, since the client may end up on either: cookies off, and a
        // timeout longer than the server's hold - here, none at all.
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = serverBaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            ["/topic/prices"] = (e, _) => { Console.WriteLine(e.Data); return Task.CompletedTask; }
        };

        var options = new BayeuxClientOptions(
            channels,
            "cometd",
            reconnectOptions: new ReconnectOptions(),
            transportType: BayeuxTransportType.Auto);

        // The fallback is not an error - the client recovers from it - so it shows only in the
        // log, as a warning. Pass an ILogger to see which transport it chose.
        await using var client = new BayeuxClient(
            http,
            options,
            NoAuthProvider.Instance,
            onDisconnected: (_, e) => Console.WriteLine($"stopped: {e.Reason}"));

        // Each ConnectAsync tries WebSocket again; inside the session, once WebSocket has worked,
        // a dropped socket is reconnected like any network failure, never swapped for long-polling.
        await client.ConnectAsync();
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}
