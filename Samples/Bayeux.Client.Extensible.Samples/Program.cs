// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Samples;

// These samples are compiled by CI so they cannot drift from the API, but they talk to a real
// server. Point them at one and uncomment the case you want to run.

var server = new Uri(args.FirstOrDefault() ?? "http://127.0.0.1:8099/");

Console.WriteLine($"""
    Bayeux.Client.Extensible samples ({server})

    Authentication
      BasicAuthExample.RunAsync                  HTTP Basic, with credential rotation
      NoAuthExample.RunOpenServerAsync           no authentication
      NoAuthExample.RunInheritedSessionAsync     session cookie from an application login
      CustomAuthExample.RunAsync                 bearer token, refreshed before it expires
      CustomAuthExample.RunRefreshingAsync       a token renewed by the provider, dropped when revoked
      CustomAuthExample.RunApiKeyAsync           API key in a header of the gateway's choosing
      CustomAuthExample.RunDelegateAsync         a provider in one lambda: gateway key + user token
      CustomAuthExample.RunSignedAsync           a provider of your own: signed requests

    Channels
      ChannelsExample.RunAsync                   wildcards, runtime subscribe, unsubscribe
      ChannelsExample.RunOverlappingAsync        what overlapping patterns deliver

    Operations
      LoggingAndReconnectExample.RunAsync        ILogger, OnError, reconnect with backoff
      WebSocketExample.RunAsync                  the same session over a WebSocket
      WebSocketExample.RunAutoAsync              WebSocket, or long-polling where it is blocked

    Extensions (ext)
      SalesforceReplayExample.RunAsync           Salesforce replay: resume after a drop or restart

    Edit Program.cs to run one.
    """);

// await BasicAuthExample.RunAsync(server, "service-account", "password");
// await NoAuthExample.RunOpenServerAsync(server);
// await NoAuthExample.RunInheritedSessionAsync(server, "the-session-cookie");
// await CustomAuthExample.RunAsync(server, _ => Task.FromResult("the-token"));
// await CustomAuthExample.RunRefreshingAsync(server,
//     _ => Task.FromResult(new AccessToken("the-token", DateTimeOffset.UtcNow.AddHours(1))));
// await CustomAuthExample.RunApiKeyAsync(server, "the-api-key");
// await CustomAuthExample.RunDelegateAsync(server, "gateway-key", "user-token", "acme");
// await CustomAuthExample.RunSignedAsync(server, "key-1", System.Text.Encoding.UTF8.GetBytes("the-shared-secret"));
// await ChannelsExample.RunAsync(server);
// await ChannelsExample.RunOverlappingAsync(server);
// await LoggingAndReconnectExample.RunAsync(server, LoggerFactory.Create(b => b.AddConsole()));
// await WebSocketExample.RunAsync(server, "service-account", "password");
// await WebSocketExample.RunAutoAsync(server);
// await SalesforceReplayExample.RunAsync(
//     new Uri("https://acme.my.salesforce.com/"), _ => Task.FromResult("the-access-token"),
//     "/event/Low_Ink__e", "replay-ids.json", TimeSpan.FromMinutes(5));
