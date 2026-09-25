// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Bayeux.Client.Extensible.Core.Models;
using Bayeux.Client.Extensible.Interfaces;

namespace Bayeux.Client.Extensible.Samples;

/// <summary>
/// Salesforce durable streaming: after a dropped session, resubscribe from the last event received
/// instead of losing everything published in between. An example of both directions of
/// <c>ext</c>, and a sample rather than part of the library because it is vendor-specific.
/// </summary>
/// <remarks>
/// <para>
/// Written from the protocol as Salesforce documents it. The conversation has four steps:
/// </para>
/// <list type="number">
/// <item>Outgoing handshake - announce support: <c>"ext": { "replay": true }</c>.</item>
/// <item>Incoming handshake reply - the server agrees with the same value, or the extension stays
/// silent for the rest of the session.</item>
/// <item>Outgoing subscribe - say where to resume each channel:
/// <c>"ext": { "replay": { "/event/X": 2113 } }</c>.</item>
/// <item>Incoming events - each carries <c>data.event.replayId</c>, which is remembered. From API
/// 68.0 the <c>/meta/connect</c> reply also carries current ids in <c>ext.replay</c>.</item>
/// </list>
/// <para>
/// Replay ids are opaque. Salesforce does not promise they are numbers or consecutive, so they are
/// kept as raw JSON and sent back exactly as received - never parsed, never computed from.
/// </para>
/// <para>
/// The poller calls <see cref="OutgoingAsync"/> and <see cref="Incoming"/> from its own loop and
/// from subscribe calls at the same time, so all state here is thread-safe.
/// </para>
/// </remarks>
public sealed class SalesforceReplayExtension : IBayeuxExt
{
    private const string ExtensionKey = "replay";

    // Meta channel names are fixed by the Bayeux protocol, and the library keeps its own copies
    // internal, so an extension written outside it spells them out.
    private const string MetaHandshake = "/meta/handshake";
    private const string MetaConnect = "/meta/connect";
    private const string MetaPrefix = "/meta/";

    // Salesforce's two special replay values.
    private static readonly JsonElement NewEventsOnly = JsonSerializer.SerializeToElement(-1);
    private static readonly JsonElement AllRetainedEvents = JsonSerializer.SerializeToElement(-2);

    private readonly ConcurrentDictionary<string, JsonElement> _replayIds = new(StringComparer.Ordinal);
    private readonly JsonElement _defaultReplayId;

    // Written by the handshake reply, read by every subscribe - possibly on another thread.
    private volatile bool _isSupported;

    /// <summary>Creates the extension.</summary>
    /// <param name="savedReplayIds">
    /// Ids saved by a previous run, from <see cref="GetReplayIds"/>, so that even a restart of the
    /// application resumes where it stopped - within Salesforce's retention window of 24 or 72
    /// hours, depending on the event type. <c>null</c> to start fresh.
    /// </param>
    /// <param name="replayAllRetained">
    /// For a channel with no saved id: <c>false</c> receives only new events; <c>true</c> receives
    /// everything still retained. Salesforce advises using the second sparingly - on a busy channel
    /// it is slow.
    /// </param>
    public SalesforceReplayExtension(
        IReadOnlyDictionary<string, JsonElement>? savedReplayIds = null,
        bool replayAllRetained = false)
    {
        _defaultReplayId = replayAllRetained ? AllRetainedEvents : NewEventsOnly;

        if (savedReplayIds is null)
            return;

        foreach (var saved in savedReplayIds)
            _replayIds[WithoutQueryString(saved.Key)] = saved.Value.Clone();
    }

    /// <summary>Whether the server agreed to replay in the current session's handshake.</summary>
    public bool IsSupported => _isSupported;

    /// <summary>
    /// The last replay id seen on each channel. Save this and pass it back to the constructor to
    /// survive an application restart.
    /// </summary>
    /// <returns>A copy, safe to serialise while events keep arriving.</returns>
    public IReadOnlyDictionary<string, JsonElement> GetReplayIds() =>
        new Dictionary<string, JsonElement>(_replayIds, StringComparer.Ordinal);

    // ----------------------------------------------------------------------------------------
    // Outgoing: the client writes ext.
    // ----------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task OutgoingAsync(BaseLongPollingRequestModel requestModel, CancellationToken cancellationToken)
    {
        if (requestModel.Channel == MetaHandshake)
        {
            // Step 1. A question, not a setting: the answer arrives in the handshake reply.
            requestModel.Ext ??= new Dictionary<string, object>();
            requestModel.Ext[ExtensionKey] = true;
        }
        else if (requestModel is SubscribeRequestModel subscribe && _isSupported)
        {
            // Step 3. Only after the server said yes. Only this subscription's entry: every message
            // in a batched subscribe gets its own, so no message speaks for another channel. Keyed
            // by the bare channel name, the same key events are stored under.
            var channel = WithoutQueryString(subscribe.Subscription);

            var resumeFrom = _replayIds.TryGetValue(channel, out var saved)
                ? saved
                : _defaultReplayId;

            subscribe.Ext ??= new Dictionary<string, object>();
            subscribe.Ext[ExtensionKey] = new Dictionary<string, JsonElement>
            {
                [channel] = resumeFrom
            };
        }

        // Nothing here waits on I/O; a cached completed task allocates nothing.
        return Task.CompletedTask;
    }

    // ----------------------------------------------------------------------------------------
    // Incoming: the client reads ext, and here also the event payload.
    // ----------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Incoming(BayeuxResponseMessageModel responseModel)
    {
        var channel = responseModel.Channel;

        if (channel is null)
            return;

        if (channel == MetaHandshake)
        {
            // Step 2. Set on every handshake, not only the first: a re-handshake can land on a
            // server that answers differently, and a stale "yes" would send ids it cannot use.
            _isSupported = TryGetReplayExt(responseModel, out var agreed)
                && agreed.ValueKind == JsonValueKind.True;
        }
        else if (channel == MetaConnect)
        {
            // Step 4, API 68.0 and later: current ids for every channel, keyed by channel name.
            if (_isSupported
                && TryGetReplayExt(responseModel, out var ids)
                && ids.ValueKind == JsonValueKind.Object)
            {
                foreach (var id in ids.EnumerateObject())
                    _replayIds[id.Name] = id.Value.Clone();
            }
        }
        else if (!channel.StartsWith(MetaPrefix, StringComparison.Ordinal) && _isSupported)
        {
            // Step 4. An event: remember its position before any handler runs, so an exception
            // in a handler does not lose it.
            if (TryGetEventReplayId(responseModel.Data, out var replayId))
                _replayIds[WithoutQueryString(channel)] = replayId.Clone();
        }
    }

    /// <summary>Reads <c>ext.replay</c> from a server message, when there is one.</summary>
    private static bool TryGetReplayExt(BayeuxResponseMessageModel message, out JsonElement value)
    {
        value = default;
        return message.Ext is { } ext && ext.TryGetValue(ExtensionKey, out value);
    }

    /// <summary>Reads <c>data.event.replayId</c>, checking the shape at every step.</summary>
    /// <remarks>
    /// <c>Data</c> of a message that has none is an undefined element, and asking it for a
    /// property throws - so every level is checked to be an object before it is read.
    /// </remarks>
    private static bool TryGetEventReplayId(JsonElement data, out JsonElement replayId)
    {
        replayId = default;

        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("event", out var @event)
            && @event.ValueKind == JsonValueKind.Object
            && @event.TryGetProperty("replayId", out replayId);
    }

    /// <summary>
    /// Strips a query string: a filtered subscription such as <c>/topic/X?Status=Open</c> is keyed
    /// by <c>/topic/X</c>. Used on both sides - storing an event's id and looking one up for a
    /// subscribe - since a key that differs between the two would never find what was saved.
    /// </summary>
    private static string WithoutQueryString(string channel)
    {
        var query = channel.IndexOf('?');
        return query < 0 ? channel : channel.Substring(0, query);
    }
}
