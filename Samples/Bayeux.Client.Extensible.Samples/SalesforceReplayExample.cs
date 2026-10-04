// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using Bayeux.Client.Extensible.Core;

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

        // A lost session is re-established by the client itself. Before each attempt: save the
        // positions - if this process dies from here on, the next run still resumes from the last
        // event it saw - and fetch a fresh token, in case the old one is the cause. The same
        // extension then resubscribes from the last id it recorded, so nothing published while we
        // were away is lost - within the retention window.
        var reconnect = new ReconnectOptions(
            maxDelay: TimeSpan.FromMinutes(1),
            // A revoked token is retried here, and only here, because a new one is fetched first;
            // the default rule refuses it to protect accounts from lockout.
            shouldRetry: e => IsRevokedToken(e) || ReconnectOptions.IsRetriable(e),
            beforeAttemptAsync: async ct =>
            {
                SaveReplayIds(replayIdsFile, replay.GetReplayIds());
                auth.UpdateCredentials(await getAccessToken(ct));
            });

        var stopped = new TaskCompletionSource<BayeuxDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var client = new BayeuxClient(
            http,
            // The API version is part of the path. Durable streaming needs 37.0 or later; ids in the
            // /meta/connect reply need 68.0.
            new BayeuxClientOptions(channels, "cometd/68.0", extensions: [replay], reconnectOptions: reconnect),
            auth,
            // Raised only when the client gives up, the server tells it to stop, or we stop it.
            onDisconnected: (_, e) => stopped.TrySetResult(e));

        await client.ConnectAsync();
        Console.WriteLine($"Replay agreed by the server: {replay.IsSupported}");

        if (await Task.WhenAny(stopped.Task, Task.Delay(runFor)) == stopped.Task)
        {
            var outcome = await stopped.Task;
            Console.WriteLine($"Stopped: {outcome.Reason} {outcome.Error?.Message}");
        }
        else
        {
            await client.DisconnectAsync();                // time is up, still connected
        }

        SaveReplayIds(replayIdsFile, replay.GetReplayIds());
    }

    /// <summary>Whether Salesforce refused because the access token is no longer valid.</summary>
    /// <remarks>
    /// Salesforce does not answer with HTTP 401. A revoked token arrives as a Bayeux refusal on
    /// <c>/meta/connect</c> with <c>401::Authentication invalid</c>, or - if it is discovered on a
    /// new handshake - as <c>403::Handshake denied</c> with the real cause under
    /// <c>ext.sfdc.failureReason</c>. Both come with advice <c>none</c>, which the default rule
    /// honours. A token is not a password, and a fresh one is fetched before every attempt, so
    /// retrying these cannot lock anything.
    /// </remarks>
    private static bool IsRevokedToken(Exception error) => error switch
    {
        BayeuxConnectException connect =>
            connect.Error?.StartsWith("401::", StringComparison.Ordinal) == true
            || FailureReason(connect.Ext)?.StartsWith("401::", StringComparison.Ordinal) == true,
        BayeuxHandshakeException handshake =>
            FailureReason(handshake.Ext)?.StartsWith("401::", StringComparison.Ordinal) == true,
        _ => false,
    };

    // Salesforce's own detail, under ext.sfdc.failureReason.
    private static string? FailureReason(IReadOnlyDictionary<string, JsonElement>? ext) =>
        ext is not null
        && ext.TryGetValue("sfdc", out var sfdc)
        && sfdc.ValueKind == JsonValueKind.Object
        && sfdc.TryGetProperty("failureReason", out var reason)
        && reason.ValueKind == JsonValueKind.String
            ? reason.GetString()
            : null;

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
