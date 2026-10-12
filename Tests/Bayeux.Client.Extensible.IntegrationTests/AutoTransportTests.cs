// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Authentication;
using Bayeux.Client.Extensible.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bayeux.Client.Extensible.IntegrationTests;

/// <summary>
/// <see cref="BayeuxTransportType.Auto"/> against servers that do and do not speak WebSocket: the
/// Java server on <c>/cometd</c>, the same server on <c>/cometd-lp</c>, which refuses the upgrade,
/// and the Node.js server, which has no WebSocket at all.
/// </summary>
/// <remarks>
/// Whether the client fell back is read from its log, the one trace the fallback leaves. Each
/// client publishes to a channel it is subscribed to and waits for its own message, which needs
/// nothing from the control plane - <c>/cometd-lp</c> has none.
/// </remarks>
[Collection(RealCometDTests.Collection)]
public sealed class AutoTransportTests
{
    private sealed class RecordingLogger : ILogger<BayeuxClient>
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Enqueue(formatter(state, exception));
        }
    }

    private static async Task<(bool fellBack, string? text)> RunAutoAsync(string variable, string path)
    {
        var url = Environment.GetEnvironmentVariable(variable);
        Skip.If(string.IsNullOrEmpty(url), $"{variable} is not set.");

        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(url!),
            Timeout = Timeout.InfiniteTimeSpan
        };

        var received = new ConcurrentQueue<JsonElement>();
        var errors = new ConcurrentQueue<BayeuxErrorEventArgs>();
        var logger = new RecordingLogger();

        var options = new BayeuxClientOptions(
            new Dictionary<string, BayeuxEventHandler>
            {
                ["/chat/auto"] = (e, _) => { received.Enqueue(e.Data); return Task.CompletedTask; }
            },
            path,
            transportType: BayeuxTransportType.Auto);

        await using var client = new BayeuxClient(http, options, NoAuthProvider.Instance, (_, _) => { }, logger: logger);
        client.OnError += (_, e) => errors.Enqueue(e);
        await client.ConnectAsync();

        await client.PublishAsync("/chat/auto", new { text = "auto" });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (received.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        // Whichever transport it ended on, it works, and nothing it recovered from was reported.
        Assert.False(received.IsEmpty);
        Assert.Empty(errors);

        var fallback = logger.Warnings.FirstOrDefault(w => w.Contains("long", StringComparison.OrdinalIgnoreCase));
        return (fallback is not null, received.TryPeek(out var data) ? data.GetProperty("text").GetString() : null);
    }

    [SkippableFact]
    public async Task Auto_stays_on_websocket_with_the_java_server()
    {
        var (fellBack, text) = await RunAutoAsync("BAYEUX_COMETD_JAVA_URL", "cometd");

        Assert.False(fellBack);
        Assert.Equal("auto", text);
    }

    [SkippableFact]
    public async Task Auto_falls_back_where_the_java_server_refuses_the_upgrade()
    {
        var (fellBack, text) = await RunAutoAsync("BAYEUX_COMETD_JAVA_URL", "cometd-lp");

        Assert.True(fellBack);
        Assert.Equal("auto", text);
    }

    [SkippableFact]
    public async Task Auto_falls_back_with_the_node_server_which_has_no_websocket()
    {
        var (fellBack, text) = await RunAutoAsync("BAYEUX_COMETD_URL", "cometd");

        Assert.True(fellBack);
        Assert.Equal("auto", text);
    }
}
