// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bayeux.Client.Extensible.Core.Constants;

namespace Bayeux.Client.Extensible.Core.Models
{
    /// <summary>What the polling loop should do after processing one <c>/meta/connect</c> response.</summary>
    public enum ConnectResult
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

    /// <summary>Why the polling loop ended.</summary>
    public enum DisconnectReason
    {
        /// <summary>
        /// The caller stopped it, through <c>DisconnectAsync</c>, <c>Dispose</c> or <c>DisposeAsync</c>.
        /// </summary>
        TokenCancellation,

        /// <summary>
        /// The server ended the session &#8212; either <c>advice.reconnect: "none"</c> or a
        /// server-sent <c>/meta/disconnect</c>. No disconnect request is sent in this case,
        /// because the session is already closed.
        /// </summary>
        ServerRequirement,

        /// <summary>
        /// An error ended the loop. The cause is carried by
        /// <see cref="Bayeux.Client.Extensible.Authentication.EventModels.OnPollerDisconnectedEventArgs.Error"/>.
        /// </summary>
        Failed
    }

    /// <summary>Which operation produced an error reported through the poller's error event.</summary>
    public enum ErrorSource
    {
        /// <summary>The HTTP request itself failed &#8212; transport, DNS, TLS or a non-success status.</summary>
        Http,

        /// <summary>
        /// The poller is misconfigured; the fix is in the caller's code rather than on the wire.
        /// </summary>
        Configuration,

        /// <summary>The <c>/meta/disconnect</c> exchange failed. Never fatal &#8212; the poller is already stopping.</summary>
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
        /// that a bug in a handler cannot stop the poller.
        /// </summary>
        Handler,

        /// <summary>
        /// An extension threw while processing a message. 
        /// Never fatal &#8212; the exception is swallowed so that a bug in an extension cannot stop the poller.
        /// </summary>
        Ext
    }

    /// <summary>Fields shared by every outgoing Bayeux message.</summary>
    public abstract class BaseLongPollingRequestModel
    {
        /// <summary>
        /// The session id issued by the handshake. Omitted from the serialized message when
        /// <c>null</c>, which is what the handshake request itself requires.
        /// </summary>
        [JsonPropertyName("clientId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ClientId { get; set; }

        /// <summary>The Bayeux meta channel this message is addressed to, for example <c>/meta/connect</c>.</summary>
        [JsonPropertyName("channel")]
        public string Channel { get; set; }

        /// <summary>Client-generated message id, echoed by the server in its reply.</summary>
        [JsonPropertyName("id")]
        public string RequestId { get; set; }

        /// <summary>
        /// The last segment of <see cref="Channel"/>, used to build the request URI
        /// (<c>/meta/connect</c> becomes <c>connect</c>). Not serialized.
        /// </summary>
        [JsonIgnore]
        public string Endpoint => Channel.Substring(Channel.LastIndexOf('/') + 1);

        /// <summary>
        /// Extension data for this message: the protocol's own extension point, keyed by extension name
        /// (for example <c>replay</c> or <c>ack</c>). Omitted from the wire when <c>null</c>, so a poller
        /// with no extensions sends exactly what it sent before this field existed.
        /// </summary>
        [JsonPropertyName("ext")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Ext { get; set; }

        /// <summary>Initialises the fields common to every Bayeux message.</summary>
        /// <param name="clientId">The session id, or <c>null</c> for a handshake.</param>
        /// <param name="channel">The meta channel being addressed.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public BaseLongPollingRequestModel(string? clientId, string channel, string requestId)
        {
            ClientId = clientId;
            Channel = channel;
            RequestId = requestId;
        }

        /// <summary>Serializes this message as the single-element JSON array Bayeux expects.</summary>
        /// <returns>The request body.</returns>
        public string FormLongPollingRequestJson() => FormLongPollingRequestJson([this]);

        /// <summary>
        /// Serializes several messages into one Bayeux request body. Every request is an array, so a
        /// batch and a single message differ only in how many elements it holds.
        /// </summary>
        /// <param name="messages">The messages to send together, in order.</param>
        /// <returns>The request body.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="messages"/> is <c>null</c>.</exception>
        /// <remarks>
        /// The copy into an <see cref="object"/> array is required: given the declared element type
        /// System.Text.Json serializes each message as a <see cref="BaseLongPollingRequestModel"/>
        /// and silently drops every property declared on the derived message.
        /// </remarks>
        public static string FormLongPollingRequestJson(IReadOnlyList<BaseLongPollingRequestModel> messages)
        {
            if (messages is null)
                throw new ArgumentNullException(nameof(messages));

            var payload = new object[messages.Count];

            for (var i = 0; i < messages.Count; i++)
                payload[i] = messages[i];

            return JsonSerializer.Serialize(payload);
        }
    }

    /// <summary>A <c>/meta/handshake</c> request, which negotiates the protocol and obtains a client id.</summary>
    public class HandshakeRequestModel : BaseLongPollingRequestModel
    {
        /// <summary>Bayeux protocol version offered by this client.</summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        /// <summary>Oldest protocol version this client accepts.</summary>
        [JsonPropertyName("minimumVersion")]
        public string MinimumVersion { get; set; } = "1.0";

        /// <summary>Transports this client can use. Only long-polling is implemented.</summary>
        [JsonPropertyName("supportedConnectionTypes")]
        public string[] SupportedConnectionTypes { get; set; } = ["long-polling"];

        /// <summary>Creates a handshake request.</summary>
        /// <param name="requestId">Client-generated message id.</param>
        public HandshakeRequestModel(string requestId) :
        base(null, CometDConstants.MetaChannels.Handshake, requestId)
        { }
    }

    /// <summary>A <c>/meta/subscribe</c> request for one channel.</summary>
    public class SubscribeRequestModel : BaseLongPollingRequestModel
    {
        /// <summary>The channel being subscribed to.</summary>
        [JsonPropertyName("subscription")]
        public string Subscription { get; set; }

        /// <summary>Creates a subscribe request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="subscription">The channel to subscribe to.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public SubscribeRequestModel(string? clientId, string subscription, string requestId) :
        base(clientId, CometDConstants.MetaChannels.Subscribe, requestId) => Subscription = subscription;
    }

    /// <summary>A <c>/meta/unsubscribe</c> request for one channel.</summary>
    public class UnsubscribeRequestModel : BaseLongPollingRequestModel
    {
        /// <summary>The channel being unsubscribed from.</summary>
        [JsonPropertyName("subscription")]
        public string Subscription { get; set; }

        /// <summary>Creates an unsubscribe request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="subscription">The channel to unsubscribe from.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public UnsubscribeRequestModel(string? clientId, string subscription, string requestId) :
        base(clientId, CometDConstants.MetaChannels.Unsubscribe, requestId) => Subscription = subscription;
    }

    /// <summary>
    /// A <c>/meta/connect</c> request. The server holds this open until it has messages or its
    /// timeout elapses &#8212; this is the long poll.
    /// </summary>
    public class ConnectRequestModel : BaseLongPollingRequestModel
    {
        /// <summary>The transport in use for this session.</summary>
        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; } = "long-polling";

        /// <summary>Creates a connect request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public ConnectRequestModel(string clientId, string requestId) :
        base(clientId, CometDConstants.MetaChannels.Connect, requestId)
        { }
    }

    /// <summary>A <c>/meta/disconnect</c> request, releasing the session on the server.</summary>
    public class DisconnectRequestModel : BaseLongPollingRequestModel
    {
        /// <summary>Creates a disconnect request.</summary>
        /// <param name="clientId">The session id to release.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public DisconnectRequestModel(string clientId, string requestId) :
        base(clientId, CometDConstants.MetaChannels.Disconnect, requestId)
        { }
    }

    /// <summary>
    /// One message from a Bayeux response. A single response carries an array of these: the reply
    /// to the request, followed by any events queued for the session.
    /// </summary>
    public class BayeuxResponseMessageModel
    {
        /// <summary>The channel this message belongs to &#8212; a meta channel for replies, a data channel for events.</summary>
        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        /// <summary>The session id. Present on handshake replies and echoed on others.</summary>
        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }

        /// <summary>The message id echoed from the request.</summary>
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        /// <summary>Whether the request succeeded. Present on replies to meta channels.</summary>
        [JsonPropertyName("successful")]
        public bool? IsSuccessful { get; set; }

        /// <summary>
        /// Whether authentication succeeded, on a handshake reply. Optional in the specification;
        /// most servers omit it, so treat only an explicit <c>false</c> as a failure.
        /// </summary>
        [JsonPropertyName("authSuccessful")]
        public bool? AuthSuccessful { get; set; }

        /// <summary>
        /// Failure description, formatted as <c>code:args:message</c> &#8212; for example
        /// <c>402::Unknown client</c>. May be an empty string on a successful message.
        /// </summary>
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        /// <summary>The channel a subscribe or unsubscribe reply refers to.</summary>
        [JsonPropertyName("subscription")]
        public string? Subscription { get; set; }

        /// <summary>The application payload of an event message.</summary>
        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }

        /// <summary>Server guidance about how the client should behave next.</summary>
        [JsonPropertyName("advice")]
        public BayeuxResponseAdviceModel? Advice { get; set; }

        /// <summary>
        /// Extension data the server attached to this message, keyed by extension name. Values are kept
        /// as raw JSON, because their shape belongs to each extension - and some, like Salesforce replay
        /// ids, must be sent back exactly as received. <c>null</c> when the server sent none.
        /// </summary>
        [JsonPropertyName("ext")]
        public Dictionary<string, JsonElement>? Ext { get; set; }
    }

    /// <summary>
    /// The Bayeux <c>advice</c> object. Advice is sticky: a server sends it only when it changes,
    /// so a client is expected to remember the last values it received.
    /// </summary>
    public class BayeuxResponseAdviceModel
    {
        /// <summary>
        /// What to do next: <c>retry</c> (poll again), <c>handshake</c> (the session is gone, start
        /// a new one) or <c>none</c> (stop and do not reconnect).
        /// </summary>
        [JsonPropertyName("reconnect")]
        public string? Reconnect { get; set; }

        /// <summary>Milliseconds to wait before sending the next <c>/meta/connect</c>.</summary>
        [JsonPropertyName("interval")]
        public int? Interval { get; set; }

        /// <summary>
        /// Milliseconds the server will hold a <c>/meta/connect</c> open. The HTTP client's timeout
        /// must exceed this, or every long poll fails on a healthy connection.
        /// </summary>
        [JsonPropertyName("timeout")]
        public int? Timeout { get; set; }

        /// <summary>
        /// Set by CometD when several sessions share one <c>BAYEUX_BROWSER</c> cookie. Only one of
        /// them keeps a real long poll; the rest are demoted to interval polling. In this library
        /// it means cookie state is being shared between pollers that should be independent.
        /// </summary>
        [JsonPropertyName("multiple-clients")]
        public bool? IsMultipleClients { get; set; }
    }
}
