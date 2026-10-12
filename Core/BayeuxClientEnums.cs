// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>How Bayeux messages travel between the client and the server.</summary>
    public enum BayeuxTransportType
    {
        /// <summary>
        /// HTTP long-polling: a POST per batch, the server holding <c>/meta/connect</c> open until it
        /// has something to send. Works through every proxy, and the only transport Salesforce offers.
        /// </summary>
        LongPolling,

        /// <summary>
        /// One WebSocket for the session: lower latency and overhead, but needs a server and every
        /// proxy on the way to allow it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Events arrive the moment the server has them, each frame on its own rather than in the
        /// reply to a <c>/meta/connect</c>. A dropped socket is replaced on the next send and the
        /// session carries on over the new one.
        /// </para>
        /// <para>
        /// Credentials and cookies go out once per socket, in the upgrade request: new credentials
        /// from <c>UpdateCredentials</c> take effect only when a new socket is opened. Of what an
        /// <c>IAuthProvider</c> sets on the request, only headers are carried over.
        /// </para>
        /// <para>
        /// The HttpClient supplies only the base address (<c>http</c> becomes <c>ws</c>,
        /// <c>https</c> becomes <c>wss</c>); its handler - proxy, certificates - is not used.
        /// On .NET Framework, WebSocket needs Windows 8 or later.
        /// </para>
        /// </remarks>
        WebSocket,

        /// <summary>
        /// WebSocket where it gets through, long-polling where it does not: for a client that must
        /// work behind proxies it does not control.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The client tries WebSocket first, and falls back to long-polling when the upgrade is
        /// refused, when the server does not offer <c>websocket</c> in the handshake, or when the
        /// socket carries the handshake but not the first <c>/meta/connect</c> - a proxy that
        /// lets the upgrade through and drops the frames. None of these is reported through
        /// <c>OnError</c>: the client recovers from them. The fallback is logged as a warning.
        /// </para>
        /// <para>
        /// Once a <c>/meta/connect</c> has been answered over WebSocket, WebSocket has proven
        /// itself: a later failure is the network's, and is retried like any other rather than
        /// falling back. Each <c>ConnectAsync</c> tries WebSocket again, so a client restarted
        /// after a proxy was fixed uses it.
        /// </para>
        /// <para>
        /// Both transports' requirements apply: the HttpClient's timeout must outlast the server's
        /// hold, as for <see cref="LongPolling"/>, since that is where the client may end up.
        /// </para>
        /// </remarks>
        Auto
    }

    /// <summary>Where a client is in its life. Reported by <c>State</c> and <c>OnStateChanged</c>.</summary>
    /// <remarks>
    /// <code>
    /// Disconnected → Connecting → Connected ⇄ Reconnecting
    ///      ↑______________|___________|____________|      (any of them, when the client stops)
    /// </code>
    /// A health check can map them directly: Connected is healthy, Reconnecting degraded,
    /// Disconnected and Connecting not yet or no longer.
    /// </remarks>
    public enum BayeuxClientState
    {
        /// <summary>
        /// No session, and no attempt to get one: before <c>ConnectAsync</c>, and after the client
        /// stops - by request, by the server, or because an error was not retried.
        /// </summary>
        Disconnected,

        /// <summary>
        /// <c>ConnectAsync</c> is handshaking and subscribing, or has finished and the first
        /// <c>/meta/connect</c> has not been answered yet.
        /// </summary>
        Connecting,

        /// <summary>A <c>/meta/connect</c> has succeeded: the session is established and polling.</summary>
        Connected,

        /// <summary>
        /// The session was interrupted - a transport failure, a refusal, or the server asking for a
        /// new handshake - and the client is getting it back on its own. Back to Connected after the
        /// next successful <c>/meta/connect</c>.
        /// </summary>
        Reconnecting
    }

    /// <summary>What the connect loop should do after processing one <c>/meta/connect</c> response.</summary>
    internal enum ConnectResult
    {
        /// <summary>Keep polling with the current session.</summary>
        Continue,

        /// <summary>
        /// The session is no longer valid. Perform a new handshake and re-subscribe, then keep polling.
        /// </summary>
        Rehandshake,

        /// <summary>
        /// The server asked the client to stop. Per the Bayeux specification the client must not
        /// retry or handshake again.
        /// </summary>
        Stop
    }

    /// <summary>Why the connect loop ended.</summary>
    public enum DisconnectReason
    {
        /// <summary>
        /// The caller stopped it, through <c>DisconnectAsync</c>, <c>Dispose</c> or <c>DisposeAsync</c>.
        /// </summary>
        TokenCancellation,

        /// <summary>
        /// The server ended the session cleanly &#8212; a successful reply advising
        /// <c>reconnect: "none"</c>, or a server-sent <c>/meta/disconnect</c>. No disconnect request
        /// is sent in this case, because the session is already closed. A <em>refused</em> connect
        /// (<c>successful: false</c> with that advice) is <see cref="Failed"/> instead, with a
        /// <see cref="BayeuxConnectException"/> carrying the server's reason.
        /// </summary>
        ServerRequirement,

        /// <summary>
        /// An error ended the loop. The cause is carried by
        /// <see cref="BayeuxDisconnectedEventArgs.Error"/>.
        /// </summary>
        Failed
    }

    /// <summary>Which operation produced an error reported through the client's error event.</summary>
    public enum ErrorSource
    {
        /// <summary>
        /// The transport failed to deliver a message or its reply: the connection could not be made
        /// or was lost, or the server answered with a non-success HTTP status.
        /// <see cref="BayeuxErrorEventArgs.Error"/> tells which - <see cref="BayeuxHttpException"/>,
        /// <see cref="HttpRequestException"/> or <see cref="System.Net.WebSockets.WebSocketException"/>.
        /// The session may well be alive: a refusal by the server is reported as
        /// <see cref="Handshake"/> or <see cref="Connect"/> instead.
        /// </summary>
        Transport,

        /// <summary>
        /// The client is misconfigured; the fix is in the caller's code rather than on the wire.
        /// </summary>
        Configuration,

        /// <summary>The <c>/meta/disconnect</c> exchange failed. Never fatal &#8212; the client is already stopping.</summary>
        Disconnect,

        /// <summary>Raised for a <c>/meta/connect</c> condition, such as the server reporting multiple clients.</summary>
        Connect,

        /// <summary>The <c>/meta/handshake</c> exchange was rejected or returned no client id.</summary>
        Handshake,

        /// <summary>A <c>/meta/subscribe</c> request was rejected.</summary>
        Subscribe,

        /// <summary>A <c>/meta/unsubscribe</c> request was rejected.</summary>
        Unsubscribe,

        /// <summary>
        /// A consumer's own message handler threw. Never fatal &#8212; the exception is swallowed so
        /// that a bug in a handler cannot stop the client.
        /// </summary>
        Handler,

        /// <summary>
        /// An extension threw while processing a message. 
        /// Never fatal &#8212; the exception is swallowed so that a bug in an extension cannot stop the client.
        /// </summary>
        Ext
    }
}
