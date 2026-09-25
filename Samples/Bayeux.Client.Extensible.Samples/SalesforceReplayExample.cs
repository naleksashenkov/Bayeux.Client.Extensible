// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using Bayeux.Client.Extensible.Authentication.EventModels;
using Bayeux.Client.Extensible.Core;
using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// Salesforce Streaming API with durable replay: a lost session - an expired access token, a
/// network drop, even a restart of this program - resumes from the last event received.
/// </summary>
public static class SalesforceReplayExample
{
    /// <summary>Subscribes to one channel and keeps its position across reconnects and restarts.</summary>
    /// <param name="myDomainUrl">The org's My Domain URL, for example <c>https://acme.my.salesforce.com/</c>.</param>
    /// <param name="getAccessToken">Obtains a fresh OAuth access token.</param>
    /// <param name="channel">A platform event or change data capture channel, such as <c>/event/Low_Ink__e</c>.</param>
    /// <param name="replayIdsFile">Where positions are saved between runs.</param>
    /// <param name="runFor">How long the sample runs before it disconnects.</param>
    public static async Task RunAsync(
        Uri myDomainUrl,
        Func<CancellationToken, Task<string>> getAccessToken,
        string channel,
        string replayIdsFile,
        TimeSpan runFor)
    {
        // Positions saved by the previous run, if there was one.
        var replay = new SalesforceReplayExtension(LoadReplayIds(replayIdsFile));

        using var http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = myDomainUrl,
            Timeout = Timeout.InfiniteTimeSpan
        };

        var channels = new Dictionary<string, BayeuxEventHandler>
        {
            [channel] = (e, _) => { Console.WriteLine($"{channel}: {e.Data}"); return Task.CompletedTask; }
        };

        // Salesforce authenticates the HTTP request, not the Bayeux message, so this is an ordinary
        // IAuthProvider. The extension handles the message level; neither knows about the other.
        var auth = new BearerTokenAuthProvider(await getAccessToken(CancellationToken.None));

        var stopped = NewStopSignal();

        await using var poller = new CometDPoller(
            http,
            // The API version is part of the path. Durable streaming needs 37.0 or later; ids in the
            // /meta/connect reply need 68.0.
            new CometdPollerOptions(channels, "cometd/68.0", extensions: [replay]),
            auth,
            // The handler reads the variable, not the object, so replacing it below re-arms it.
            onPollerDisconnected: (_, e) => stopped.TrySetResult(e));

        await poller.ConnectAsync();
        Console.WriteLine($"Replay agreed by the server: {replay.IsSupported}");

        var deadline = DateTime.UtcNow + runFor;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero)
                break;

            if (await Task.WhenAny(stopped.Task, Task.Delay(remaining)) != stopped.Task)
                break;                                     // time is up, still connected

            var outcome = await stopped.Task;

            // Saved at every stop, not only at the end: if this process dies from here on, the next
            // run still resumes from the last event it saw.
            SaveReplayIds(replayIdsFile, replay.GetReplayIds());

            // The server told us to stop, or we did. Neither is a reason to reconnect.
            if (outcome.Reason != DisconnectReason.Failed)
                return;

            // Usually an expired access token. A fresh token for the same provider, the same
            // extension: the resubscribe asks for everything after the last id it recorded, so
            // nothing published while we were away is lost - within the retention window.
            Console.WriteLine($"Session lost ({outcome.Error?.Message}); reconnecting.");

            auth.UpdateCredentials(await getAccessToken(CancellationToken.None));
            stopped = NewStopSignal();

            await Task.Delay(TimeSpan.FromSeconds(5));      // never retry as fast as the server refuses
            await poller.ConnectAsync();
        }

        await poller.DisconnectAsync();
        SaveReplayIds(replayIdsFile, replay.GetReplayIds());
    }

    private static TaskCompletionSource<OnPollerDisconnectedEventArgs> NewStopSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static IReadOnlyDictionary<string, JsonElement>? LoadReplayIds(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))
            : null;

    /// <summary>Writes the positions to a temporary file first, then moves it into place.</summary>
    /// <remarks>
    /// A crash halfway through writing the file in place would destroy the only copy of the
    /// positions; a move within one directory either happens entirely or not at all.
    /// </remarks>
    private static void SaveReplayIds(string path, IReadOnlyDictionary<string, JsonElement> ids)
    {
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(ids));
        File.Move(temporary, path, overwrite: true);
    }
}
