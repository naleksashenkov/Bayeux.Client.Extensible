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
/// A minimal Bayeux server over <see cref="HttpListener"/>. Lets a test drive the real poller
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

    // The poller keeps recording while a test reads, so every observation is a snapshot.
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

            // A Bayeux request is always an array, and this client batches subscribes and
            // unsubscribes into one. Answer every message in it, not just the first.
            var reply = new JsonArray();

            foreach (var node in JsonNode.Parse(body)!.AsArray())
            {
                var request = node!.AsObject();
                var id = request["id"]?.GetValue<string>();

                var messages = path switch
                {
                    "/cometd/handshake" => Handshake(ctx, id),
                    "/cometd/subscribe" => Subscribe(request, id),
                    "/cometd/unsubscribe" => Unsubscribe(request, id),
                    "/cometd/connect" => await ConnectAsync(request, id).ConfigureAwait(false),
                    "/cometd/disconnect" => Disconnect(id),
                    _ => new JsonArray()
                };

                // Re-parsed because a JsonNode cannot belong to two parents.
                foreach (var message in messages)
                    reply.Add(JsonNode.Parse(message!.ToJsonString()));
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

    private JsonArray Handshake(HttpListenerContext ctx, string? id)
    {
        Interlocked.Increment(ref HandshakeCount);
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

    private async Task<JsonArray> ConnectAsync(JsonObject request, string? id)
    {
        if (StopOnNextConnect)
        {
            StopOnNextConnect = false;
            return new JsonArray(new JsonObject
            {
                ["channel"] = "/meta/connect",
                ["successful"] = false,
                ["error"] = "403::Forbidden",
                ["id"] = id,
                ["advice"] = new JsonObject { ["reconnect"] = "none" }
            });
        }

        if (FailNextConnectsWith402 > 0)
        {
            FailNextConnectsWith402--;
            return new JsonArray(new JsonObject
            {
                ["channel"] = "/meta/connect",
                ["successful"] = false,
                ["error"] = "402::Unknown client",
                ["id"] = id,
                ["advice"] = new JsonObject { ["reconnect"] = "handshake", ["interval"] = 0 }
            });
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

        // Emulate the long poll: hold briefly, then flush whatever is queued.
        for (var i = 0; i < 40 && !_cts.IsCancellationRequested; i++)
        {
            if (IsCurrent() && !PendingEvents.IsEmpty) break;
            await Task.Delay(25).ConfigureAwait(false);
        }

        if (IsCurrent())
            while (PendingEvents.TryDequeue(out var queued))
                reply.Add(queued);

        return reply;
    }

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
