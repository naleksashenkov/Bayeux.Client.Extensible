// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bayeux.Client.Extensible.Tests;

/// <summary>
/// A minimal Bayeux server over <see cref="HttpListener"/>. Lets a test drive the real client
/// over real HTTP and script protocol conditions that a live server will not produce on demand.
/// </summary>
public sealed class FakeBayeuxServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private int _clientSeq;
    private volatile string _currentClientId = string.Empty;

    public string Prefix { get; }

    // Observed by tests.
    public List<string> RequestBodies { get; } = new();
    public List<string?> AuthHeaders { get; } = new();
    public List<string?> CookieHeaders { get; } = new();
    public List<string> Subscriptions { get; } = new();
    public List<string> Unsubscriptions { get; } = new();
    public int HandshakeCount;
    public int DisconnectCount;

    // Scripted behaviour.
    //
    // Every knob below is written by the test thread and read by a listener thread, so all of it
    // goes through _sync. A plain auto-property here is a visibility race that shows up as a test
    // that passes in Debug and fails in Release, once, on someone else's machine.
    public ConcurrentQueue<JsonObject> PendingEvents { get; } = new();

    private int _failNextConnectsWith402;
    private bool _stopOnNextConnect;
    private bool _sendMultipleClients;
    private bool _rejectSubscribe;
    private int _adviceTimeoutMs = 30000;
    private (string Channel, object Data)? _piggybackOnNextSubscribe;
    private int? _nextSubscribeHttpStatus;
    private int? _nextUnsubscribeHttpStatus;
    private string? _handshakeExtJson;
    private int _failNextConnectsWithStatus;
    private int _connectFailureStatus = 500;
    private int _rejectNextHandshakes;
    private string? _rejectedHandshakeReconnect = "none";
    private readonly Queue<int> _scriptedConnects = new();
    private (string Error, string? ExtJson)? _connectRefusal;
    private string? _rejectedHandshakeExtJson;
    private readonly HashSet<string> _rejectedSubscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejectedUnsubscriptions = new(StringComparer.Ordinal);

    public int FailNextConnectsWith402
    {
        get { lock (_sync) return _failNextConnectsWith402; }
        set { lock (_sync) _failNextConnectsWith402 = value; }
    }

    public bool StopOnNextConnect
    {
        get { lock (_sync) return _stopOnNextConnect; }
        set { lock (_sync) _stopOnNextConnect = value; }
    }

    public bool SendMultipleClients
    {
        get { lock (_sync) return _sendMultipleClients; }
        set { lock (_sync) _sendMultipleClients = value; }
    }

    public bool RejectSubscribe
    {
        get { lock (_sync) return _rejectSubscribe; }
        set { lock (_sync) _rejectSubscribe = value; }
    }

    public int AdviceTimeoutMs
    {
        get { lock (_sync) return _adviceTimeoutMs; }
        set { lock (_sync) _adviceTimeoutMs = value; }
    }

    /// <summary>
    /// An event to put in front of the next <c>/meta/subscribe</c> reply, as CometD does when the
    /// session queue is flushed onto a request that is not <c>/meta/connect</c>.
    /// </summary>
    public (string Channel, object Data)? PiggybackOnNextSubscribe
    {
        get { lock (_sync) return _piggybackOnNextSubscribe; }
        set { lock (_sync) _piggybackOnNextSubscribe = value; }
    }

    /// <summary>Fails the next subscribe request at the HTTP level instead of answering it.</summary>
    public int? NextSubscribeHttpStatus
    {
        get { lock (_sync) return _nextSubscribeHttpStatus; }
        set { lock (_sync) _nextSubscribeHttpStatus = value; }
    }

    /// <summary>Fails the next unsubscribe request at the HTTP level instead of answering it.</summary>
    public int? NextUnsubscribeHttpStatus
    {
        get { lock (_sync) return _nextUnsubscribeHttpStatus; }
        set { lock (_sync) _nextUnsubscribeHttpStatus = value; }
    }

    /// <summary>
    /// JSON for the <c>ext</c> field of every handshake reply, as a server agreeing to an
    /// extension would send it. <c>null</c> for none.
    /// </summary>
    public string? HandshakeExtJson
    {
        get { lock (_sync) return _handshakeExtJson; }
        set { lock (_sync) _handshakeExtJson = value; }
    }

    /// <summary>
    /// How many of the next <c>/meta/connect</c> requests fail at the HTTP level with
    /// <see cref="ConnectFailureStatus"/> - a restarting server, a proxy error - instead of being
    /// answered.
    /// </summary>
    public int FailNextConnectsWithStatus
    {
        get { lock (_sync) return _failNextConnectsWithStatus; }
        set { lock (_sync) _failNextConnectsWithStatus = value; }
    }

    /// <summary>The HTTP status for <see cref="FailNextConnectsWithStatus"/>. 500 by default.</summary>
    public int ConnectFailureStatus
    {
        get { lock (_sync) return _connectFailureStatus; }
        set { lock (_sync) _connectFailureStatus = value; }
    }

    /// <summary>
    /// How many of the next handshakes are refused with <c>successful: false</c>, as a server
    /// does for rejected credentials. Each refusal still counts in <see cref="HandshakeCount"/>.
    /// </summary>
    public int RejectNextHandshakes
    {
        get { lock (_sync) return _rejectNextHandshakes; }
        set { lock (_sync) _rejectNextHandshakes = value; }
    }

    /// <summary>
    /// <c>advice.reconnect</c> sent with a refused handshake: <c>none</c> by default, as CometD
    /// sends when its security policy denies one; <c>null</c> sends no advice at all.
    /// </summary>
    public string? RejectedHandshakeReconnect
    {
        get { lock (_sync) return _rejectedHandshakeReconnect; }
        set { lock (_sync) _rejectedHandshakeReconnect = value; }
    }

    /// <summary>
    /// Refuses the next <c>/meta/connect</c> with <c>successful: false</c>, this error and advice
    /// <c>none</c> - as Salesforce does with <c>401::Authentication invalid</c> for a revoked
    /// token. Unlike <see cref="StopOnNextConnect"/>, which is a clean stop.
    /// </summary>
    /// <param name="error">The Bayeux error string.</param>
    /// <param name="extJson">JSON for the reply's <c>ext</c>, or <c>null</c> for none.</param>
    public void RefuseNextConnect(string error, string? extJson = null)
    {
        lock (_sync) _connectRefusal = (error, extJson);
    }

    private bool TakeConnectRefusal(out (string Error, string? ExtJson) refusal)
    {
        lock (_sync)
        {
            refusal = _connectRefusal ?? default;

            if (_connectRefusal is null)
                return false;

            _connectRefusal = null;
            return true;
        }
    }

    /// <summary>
    /// JSON for the <c>ext</c> of a refused handshake - where Salesforce puts the real cause under
    /// <c>sfdc.failureReason</c>. <c>null</c> for none.
    /// </summary>
    public string? RejectedHandshakeExtJson
    {
        get { lock (_sync) return _rejectedHandshakeExtJson; }
        set { lock (_sync) _rejectedHandshakeExtJson = value; }
    }

    /// <summary>
    /// Answers the next <c>/meta/connect</c> requests in this order, one entry each: 200 as
    /// usual, 402 with the Bayeux "unknown client" reply, anything else as that HTTP status.
    /// Takes precedence over every other connect setting; once used up, those apply again.
    /// </summary>
    /// <remarks>
    /// For sequences the counters cannot express, such as a failure, then a 402, then failures
    /// again - the counters always play out one kind before the next.
    /// </remarks>
    public void ScriptConnects(params int[] responses)
    {
        lock (_sync)
            foreach (var response in responses) _scriptedConnects.Enqueue(response);
    }

    private int? TakeScriptedConnect()
    {
        lock (_sync)
            return _scriptedConnects.Count > 0 ? _scriptedConnects.Dequeue() : null;
    }

    // Read and decrement in one step: the client can send the next request before a test thread
    // would otherwise see the count go down.
    private bool TakeConnectFailure(out int status)
    {
        lock (_sync)
        {
            status = _connectFailureStatus;

            if (_failNextConnectsWithStatus <= 0)
                return false;

            _failNextConnectsWithStatus--;
            return true;
        }
    }

    private bool TakeHandshakeRejection(out string? reconnect)
    {
        lock (_sync)
        {
            reconnect = _rejectedHandshakeReconnect;

            if (_rejectNextHandshakes <= 0)
                return false;

            _rejectNextHandshakes--;
            return true;
        }
    }

    /// <summary>Makes this server reject a <c>/meta/subscribe</c> for each of these channels.</summary>
    public void RejectSubscribeFor(params string[] channels)
    {
        lock (_sync)
            foreach (var channel in channels) _rejectedSubscriptions.Add(channel);
    }

    /// <summary>Makes this server reject a <c>/meta/unsubscribe</c> for each of these channels.</summary>
    public void RejectUnsubscribeFor(params string[] channels)
    {
        lock (_sync)
            foreach (var channel in channels) _rejectedUnsubscriptions.Add(channel);
    }

    // ---- acknowledgements ----------------------------------------------------------------------

    private bool _ackEnabled;
    private bool _ackSession;
    private long _ackBatch;
    private bool _loseNextReplyWithEvents;
    private readonly List<(long Batch, JsonObject Message)> _unacknowledged = new();
    private readonly List<long?> _acksReceived = new();

    /// <summary>
    /// Whether this server supports the acknowledge extension. When on and the client asks for it
    /// in the handshake, connect replies carry batch numbers, and events stay queued until the
    /// client confirms them.
    /// </summary>
    public bool AckEnabled
    {
        get { lock (_sync) return _ackEnabled; }
        set { lock (_sync) _ackEnabled = value; }
    }

    /// <summary>
    /// Fails the next connect reply that carries events with HTTP 500 instead of sending it: the
    /// events were taken off the queue, but never reached the client. A network failure at the
    /// worst moment, which is exactly what acknowledgements exist for.
    /// </summary>
    public bool LoseNextReplyWithEvents
    {
        get { lock (_sync) return _loseNextReplyWithEvents; }
        set { lock (_sync) _loseNextReplyWithEvents = value; }
    }

    /// <summary>
    /// The <c>ext.ack</c> of every connect in the acknowledged session, in order: what the client
    /// confirmed each time, or <c>null</c> when it sent none.
    /// </summary>
    public long?[] AcksReceivedSnapshot
    {
        get { lock (_sync) return _acksReceived.ToArray(); }
    }

    private bool IsAckSession
    {
        get { lock (_sync) return _ackSession; }
    }

    private bool HasUnacknowledged
    {
        get { lock (_sync) return _ackSession && _unacknowledged.Count > 0; }
    }

    private bool TakeLoseNextReply()
    {
        lock (_sync)
        {
            if (!_loseNextReplyWithEvents)
                return false;

            _loseNextReplyWithEvents = false;
            return true;
        }
    }

    // ---- publishing ----------------------------------------------------------------------------

    private readonly List<JsonObject> _publishes = new();
    private readonly HashSet<string> _rejectedPublishes = new(StringComparer.Ordinal);
    private int? _nextPublishHttpStatus;
    private bool _omitNextPublishReply;
    private bool _echoPublishesInReply;

    /// <summary>Every message the client published, as received, in order.</summary>
    public JsonObject[] PublishesSnapshot
    {
        get { lock (_sync) return _publishes.Select(p => JsonNode.Parse(p.ToJsonString())!.AsObject()).ToArray(); }
    }

    /// <summary>Makes this server refuse a publish to each of these channels.</summary>
    public void RejectPublishFor(params string[] channels)
    {
        lock (_sync)
            foreach (var channel in channels) _rejectedPublishes.Add(channel);
    }

    /// <summary>Fails the next publish at the HTTP level instead of answering it.</summary>
    public int? NextPublishHttpStatus
    {
        get { lock (_sync) return _nextPublishHttpStatus; }
        set { lock (_sync) _nextPublishHttpStatus = value; }
    }

    /// <summary>Answers the next publish with an empty array, as no conforming server would.</summary>
    public bool OmitNextPublishReply
    {
        get { lock (_sync) return _omitNextPublishReply; }
        set { lock (_sync) _omitNextPublishReply = value; }
    }

    /// <summary>
    /// Delivers an accepted publish back in the same reply, ahead of the acknowledgement - what
    /// CometD does when the publisher is itself subscribed and the session queue is flushed onto
    /// the publish response. Off by default: the message then waits for the next connect.
    /// </summary>
    public bool EchoPublishesInReply
    {
        get { lock (_sync) return _echoPublishesInReply; }
        set { lock (_sync) _echoPublishesInReply = value; }
    }

    private bool IsPublishRejected(string channel)
    {
        lock (_sync) return _rejectedPublishes.Contains(channel);
    }

    private bool IsSubscribeRejected(string channel)
    {
        lock (_sync) return _rejectSubscribe || _rejectedSubscriptions.Contains(channel);
    }

    private bool IsUnsubscribeRejected(string channel)
    {
        lock (_sync) return _rejectedUnsubscriptions.Contains(channel);
    }

    public FakeBayeuxServer()
    {
        Prefix = $"http://127.0.0.1:{FreePort()}/";
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        // Stopped by hand rather than with "using": TcpListener is not IDisposable on .NET Framework.
        var probe = new TcpListener(IPAddress.Loopback, 0);

        try
        {
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public HttpClient CreateClient(TimeSpan? timeout = null) =>
        new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(Prefix),
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };

    public void Publish(string channel, object data, string? extJson = null)
    {
        var message = new JsonObject
        {
            ["channel"] = channel,
            ["data"] = JsonNode.Parse(JsonSerializer.Serialize(data))
        };

        if (extJson is not null)
            message["ext"] = JsonNode.Parse(extJson);

        PendingEvents.Enqueue(message);
    }

    public string BodyFor(string endpoint)
    {
        lock (_sync)
            return RequestBodies.First(b => b.StartsWith("/cometd/" + endpoint, StringComparison.Ordinal));
    }

    // The client keeps recording while a test reads, so every observation is a snapshot.
    public string?[] AuthHeadersSnapshot { get { lock (_sync) return AuthHeaders.ToArray(); } }

    public string?[] CookieHeadersSnapshot { get { lock (_sync) return CookieHeaders.ToArray(); } }

    public string[] SubscriptionsSnapshot { get { lock (_sync) return Subscriptions.ToArray(); } }

    public string[] UnsubscriptionsSnapshot { get { lock (_sync) return Unsubscriptions.ToArray(); } }

    public string[] RequestBodiesSnapshot { get { lock (_sync) return RequestBodies.ToArray(); } }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            lock (_sync)
            {
                RequestBodies.Add($"{path} {body}");
                AuthHeaders.Add(ctx.Request.Headers["Authorization"]);
                CookieHeaders.Add(ctx.Request.Headers["Cookie"]);
            }

            if (path is "/cometd/subscribe" && NextSubscribeHttpStatus is { } subscribeStatus)
            {
                NextSubscribeHttpStatus = null;
                ctx.Response.StatusCode = subscribeStatus;
                ctx.Response.Close();
                return;
            }

            if (path is "/cometd/unsubscribe" && NextUnsubscribeHttpStatus is { } unsubscribeStatus)
            {
                NextUnsubscribeHttpStatus = null;
                ctx.Response.StatusCode = unsubscribeStatus;
                ctx.Response.Close();
                return;
            }

            if (path is "/cometd" && NextPublishHttpStatus is { } publishStatus)
            {
                NextPublishHttpStatus = null;
                ctx.Response.StatusCode = publishStatus;
                ctx.Response.Close();
                return;
            }

            var scripted = path is "/cometd/connect" ? TakeScriptedConnect() : null;

            if (scripted is { } scriptedStatus && scriptedStatus != 200 && scriptedStatus != 402)
            {
                ctx.Response.StatusCode = scriptedStatus;
                ctx.Response.Close();
                return;
            }

            if (scripted is null && path is "/cometd/connect" && TakeConnectFailure(out var connectStatus))
            {
                ctx.Response.StatusCode = connectStatus;
                ctx.Response.Close();
                return;
            }

            // A Bayeux request is always an array, and this client batches subscribes and
            // unsubscribes into one. Answer every message in it, not just the first.
            var reply = new JsonArray();

            foreach (var node in JsonNode.Parse(body)!.AsArray())
            {
                var request = node!.AsObject();
                var id = request["id"]?.GetValue<string>();

                var messages = path switch
                {
                    "/cometd/handshake" => Handshake(ctx, request, id),
                    "/cometd/subscribe" => Subscribe(request, id),
                    "/cometd/unsubscribe" => Unsubscribe(request, id),
                    "/cometd/connect" => await ConnectAsync(request, id, scripted).ConfigureAwait(false),
                    "/cometd/disconnect" => Disconnect(id),
                    // Application messages go to the base path; meta messages carry their type.
                    "/cometd" => HandlePublish(request, id),
                    _ => new JsonArray()
                };

                // Re-parsed because a JsonNode cannot belong to two parents.
                foreach (var message in messages)
                    reply.Add(JsonNode.Parse(message!.ToJsonString()));
            }

            // The reply is ready and its events are off the queue; then the connection fails.
            if (path is "/cometd/connect" && reply.Count > 1 && TakeLoseNextReply())
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
                return;
            }

            var payload = Encoding.UTF8.GetBytes(reply.ToJsonString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = payload.Length;
            // The offset/count overload, not the span one: .NET Framework has no Memory<byte> overload.
            await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* client gone */ }
        }
    }

    private JsonArray HandlePublish(JsonObject request, string? id)
    {
        var channel = request["channel"]!.GetValue<string>();

        lock (_sync) _publishes.Add(JsonNode.Parse(request.ToJsonString())!.AsObject());

        if (OmitNextPublishReply)
        {
            OmitNextPublishReply = false;
            return new JsonArray();
        }

        if (IsPublishRejected(channel))
            return new JsonArray(new JsonObject
            {
                ["channel"] = channel,
                ["successful"] = false,
                ["error"] = $"403:{channel}:Publish denied",
                ["id"] = id
            });

        // Broadcast: the message reaches subscribers - the publisher among them - as an event
        // with data and no "successful".
        var delivered = new JsonObject
        {
            ["channel"] = channel,
            ["data"] = request["data"] is { } data ? JsonNode.Parse(data.ToJsonString()) : null
        };

        var reply = new JsonArray();

        if (EchoPublishesInReply)
            reply.Add(delivered);
        else
            PendingEvents.Enqueue(delivered);

        // The acknowledgement: the same channel and id, "successful", no data.
        reply.Add(new JsonObject
        {
            ["channel"] = channel,
            ["successful"] = true,
            ["id"] = id
        });

        return reply;
    }

    private JsonArray Handshake(HttpListenerContext ctx, JsonObject request, string? id)
    {
        Interlocked.Increment(ref HandshakeCount);

        if (TakeHandshakeRejection(out var reconnect))
        {
            var refusal = new JsonObject
            {
                ["channel"] = "/meta/handshake",
                ["successful"] = false,
                ["error"] = "403::Handshake denied",
                ["id"] = id
            };

            if (reconnect is not null)
                refusal["advice"] = new JsonObject { ["reconnect"] = reconnect, ["interval"] = 0 };

            if (RejectedHandshakeExtJson is { } refusalExt)
                refusal["ext"] = JsonNode.Parse(refusalExt);

            return new JsonArray(refusal);
        }

        var clientId = "cid-" + Interlocked.Increment(ref _clientSeq);
        _currentClientId = clientId;

        ctx.Response.Headers.Add("Set-Cookie", $"BAYEUX_BROWSER={clientId}-browser; Path=/; HttpOnly");

        var reply = new JsonObject
        {
            ["channel"] = "/meta/handshake",
            ["version"] = "1.0",
            ["supportedConnectionTypes"] = new JsonArray("long-polling"),
            ["clientId"] = clientId,
            ["successful"] = true,
            ["id"] = id,
            ["advice"] = new JsonObject
            {
                ["reconnect"] = "retry",
                ["interval"] = 0,
                ["timeout"] = AdviceTimeoutMs
            }
        };

        if (HandshakeExtJson is { } ext)
            reply["ext"] = JsonNode.Parse(ext);

        // Acknowledgements, when both sides want them. A new session starts with an empty queue.
        var clientAsksForAck = request["ext"]?["ack"] is JsonValue asked && asked.TryGetValue<bool>(out var yes) && yes;

        lock (_sync)
        {
            _ackSession = AckEnabled && clientAsksForAck;
            _unacknowledged.Clear();
            _ackBatch = 0;
        }

        if (IsAckSession)
        {
            reply["ext"] ??= new JsonObject();
            reply["ext"]!["ack"] = true;
        }

        return new JsonArray(reply);
    }

    private JsonArray Subscribe(JsonObject request, string? id)
    {
        var channel = request["subscription"]!.GetValue<string>();

        JsonObject? piggyback = null;

        if (PiggybackOnNextSubscribe is { } queued)
        {
            PiggybackOnNextSubscribe = null;

            piggyback = new JsonObject
            {
                ["channel"] = queued.Channel,
                ["data"] = JsonNode.Parse(JsonSerializer.Serialize(queued.Data))
            };
        }

        var reply = new JsonArray();

        // The queue goes in front of the reply, which is the order the reference server uses.
        if (piggyback != null)
            reply.Add(piggyback);

        if (IsSubscribeRejected(channel))
        {
            reply.Add(new JsonObject
            {
                ["channel"] = "/meta/subscribe",
                ["subscription"] = channel,
                ["successful"] = false,
                ["error"] = $"403:{channel}:Subscription denied",
                ["id"] = id
            });

            return reply;
        }

        lock (_sync) Subscriptions.Add(channel);

        reply.Add(new JsonObject
        {
            ["channel"] = "/meta/subscribe",
            ["clientId"] = request["clientId"]?.GetValue<string>(),
            ["subscription"] = channel,
            ["successful"] = true,
            ["error"] = "",
            ["id"] = id
        });

        return reply;
    }

    private JsonArray Unsubscribe(JsonObject request, string? id)
    {
        var channel = request["subscription"]!.GetValue<string>();

        if (IsUnsubscribeRejected(channel))
            return new JsonArray(new JsonObject
            {
                ["channel"] = "/meta/unsubscribe",
                ["subscription"] = channel,
                ["successful"] = false,
                ["error"] = $"403:{channel}:Unsubscribe denied",
                ["id"] = id
            });

        lock (_sync) Unsubscriptions.Add(channel);

        return new JsonArray(new JsonObject
        {
            ["channel"] = "/meta/unsubscribe",
            ["clientId"] = request["clientId"]?.GetValue<string>(),
            ["subscription"] = channel,
            ["successful"] = true,
            ["error"] = "",
            ["id"] = id
        });
    }

    /// <param name="scripted">The entry from <see cref="ScriptConnects"/> for this request, if any.</param>
    private async Task<JsonArray> ConnectAsync(JsonObject request, string? id, int? scripted)
    {
        if (scripted == 402)
            return Reply402(id);

        // A clean stop: the connect succeeded, and the server advises not to reconnect.
        if (scripted is null && StopOnNextConnect)
        {
            StopOnNextConnect = false;
            return new JsonArray(new JsonObject
            {
                ["channel"] = "/meta/connect",
                ["clientId"] = request["clientId"]?.GetValue<string>(),
                ["successful"] = true,
                ["id"] = id,
                ["advice"] = new JsonObject { ["reconnect"] = "none" }
            });
        }

        // A refusal: the connect failed, with the same advice.
        if (scripted is null && TakeConnectRefusal(out var refusal))
        {
            var refused = new JsonObject
            {
                ["channel"] = "/meta/connect",
                ["clientId"] = request["clientId"]?.GetValue<string>(),
                ["successful"] = false,
                ["error"] = refusal.Error,
                ["id"] = id,
                ["advice"] = new JsonObject { ["reconnect"] = "none", ["interval"] = 0 }
            };

            if (refusal.ExtJson is not null)
                refused["ext"] = JsonNode.Parse(refusal.ExtJson);

            return new JsonArray(refused);
        }

        if (scripted is null && FailNextConnectsWith402 > 0)
        {
            FailNextConnectsWith402--;
            return Reply402(id);
        }

        var advice = new JsonObject { ["reconnect"] = "retry", ["interval"] = 0 };
        if (SendMultipleClients)
            advice["multiple-clients"] = true;

        var reply = new JsonArray(new JsonObject
        {
            ["channel"] = "/meta/connect",
            ["clientId"] = request["clientId"]?.GetValue<string>(),
            ["successful"] = true,
            ["error"] = "",
            ["id"] = id,
            ["advice"] = advice
        });

        // Queues are per session in a real server. This must be re-evaluated as the poll runs:
        // a connect left over from a previous session was current when it arrived, and would
        // otherwise drain events meant for the new session into a response nobody reads.
        var clientId = request["clientId"]?.GetValue<string>();
        bool IsCurrent() => clientId == _currentClientId;

        // Emulate the long poll: hold briefly, then flush whatever is queued - including, with
        // acknowledgements, anything sent before and not yet confirmed.
        for (var i = 0; i < 40 && !_cts.IsCancellationRequested; i++)
        {
            if (IsCurrent() && (!PendingEvents.IsEmpty || HasUnacknowledged)) break;
            await Task.Delay(25).ConfigureAwait(false);
        }

        if (!IsCurrent())
            return reply;

        if (!IsAckSession)
        {
            while (PendingEvents.TryDequeue(out var queued))
                reply.Add(queued);

            return reply;
        }

        // Acknowledgements: what the client confirmed is dropped; what it has not is sent again,
        // together with anything new, as the latest batch.
        var confirmed = request["ext"]?["ack"]?.GetValue<long>();

        lock (_sync)
        {
            _acksReceived.Add(confirmed);

            if (confirmed is { } upTo)
                _unacknowledged.RemoveAll(entry => entry.Batch <= upTo);

            var fresh = new List<JsonObject>();
            while (PendingEvents.TryDequeue(out var queued))
                fresh.Add(queued);

            if (fresh.Count > 0)
            {
                _ackBatch++;
                _unacknowledged.AddRange(fresh.Select(message => (_ackBatch, message)));
            }

            foreach (var (_, message) in _unacknowledged)
                reply.Add(JsonNode.Parse(message.ToJsonString()));

            if (_unacknowledged.Count > 0)
                reply[0]!["ext"] = new JsonObject { ["ack"] = _ackBatch };
        }

        return reply;
    }

    // The server no longer knows the session and asks for a new handshake.
    private static JsonArray Reply402(string? id) =>
        new(new JsonObject
        {
            ["channel"] = "/meta/connect",
            ["successful"] = false,
            ["error"] = "402::Unknown client",
            ["id"] = id,
            ["advice"] = new JsonObject { ["reconnect"] = "handshake", ["interval"] = 0 }
        });

    private JsonArray Disconnect(string? id)
    {
        Interlocked.Increment(ref DisconnectCount);

        return new JsonArray(new JsonObject
        {
            ["channel"] = "/meta/disconnect",
            ["successful"] = true,
            ["id"] = id
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
        _cts.Dispose();
    }
}
