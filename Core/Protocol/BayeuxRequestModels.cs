// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>Fields shared by every outgoing Bayeux message.</summary>
    public abstract class BayeuxRequestModel
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
        /// What is appended to the endpoint path for this message: the last segment of a meta
        /// channel (<c>/meta/connect</c> becomes <c>connect</c>), or <c>null</c> for an application
        /// message, which goes to the base path itself. Not serialized.
        /// </summary>
        /// <remarks>
        /// CometD clients append the type for meta messages only. Appending the last segment of an
        /// application channel would send a publish on <c>/chat/room1</c> to <c>{path}/room1</c>,
        /// which a server accepts only by the accident of mapping everything below its path.
        /// </remarks>
        [JsonIgnore]
        public string? Endpoint => Channel.StartsWith(BayeuxConstants.MetaChannels.Meta, StringComparison.Ordinal) 
            ? Channel.Substring(Channel.LastIndexOf('/') + 1)
            : null;

        /// <summary>
        /// Extension data for this message: the protocol's own extension point, keyed by extension name
        /// (for example <c>replay</c> or <c>ack</c>). Omitted from the wire when <c>null</c>, so a client
        /// with no extensions sends exactly what it sent before this field existed.
        /// </summary>
        [JsonPropertyName("ext")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Ext { get; set; }

        /// <summary>Initialises the fields common to every Bayeux message.</summary>
        /// <param name="clientId">The session id, or <c>null</c> for a handshake.</param>
        /// <param name="channel">The meta channel being addressed.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public BayeuxRequestModel(string? clientId, string channel, string requestId)
        {
            ClientId = clientId;
            Channel = channel;
            RequestId = requestId;
        }

        /// <summary>Serializes this message as the single-element JSON array Bayeux expects.</summary>
        /// <returns>The request body, or the WebSocket frame.</returns>
        public string FormRequestJson() => FormRequestJson([this]);

        /// <summary>
        /// Serializes several messages into one Bayeux request body. Every request is an array, so a
        /// batch and a single message differ only in how many elements it holds.
        /// </summary>
        /// <param name="messages">The messages to send together, in order.</param>
        /// <returns>The request body, or the WebSocket frame: both transports send the same JSON.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="messages"/> is <c>null</c>.</exception>
        /// <remarks>
        /// The copy into an <see cref="object"/> array is required: given the declared element type
        /// System.Text.Json serializes each message as a <see cref="BayeuxRequestModel"/>
        /// and silently drops every property declared on the derived message.
        /// </remarks>
        public static string FormRequestJson(IReadOnlyList<BayeuxRequestModel> messages)
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
    public class HandshakeRequestModel : BayeuxRequestModel
    {
        /// <summary>Bayeux protocol version offered by this client.</summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        /// <summary>Oldest protocol version this client accepts.</summary>
        [JsonPropertyName("minimumVersion")]
        public string MinimumVersion { get; set; } = "1.0";

        /// <summary>
        /// The transports this client offers: the one it was configured with, or both with
        /// <see cref="BayeuxTransportType.Auto"/>.
        /// </summary>
        [JsonPropertyName("supportedConnectionTypes")]
        public string[] SupportedConnectionTypes { get; set; }

        /// <summary>Creates a handshake request.</summary>
        /// <param name="requestId">Client-generated message id.</param>
        /// <param name="supportedConnectionTypes">
        /// The protocol's names for the transports the client can use, such as <c>long-polling</c>
        /// and <c>websocket</c>.
        /// </param>
        public HandshakeRequestModel(string requestId, params string[] supportedConnectionTypes)
        : base(null, BayeuxConstants.MetaChannels.Handshake, requestId) => SupportedConnectionTypes = supportedConnectionTypes;
    }

    /// <summary>A <c>/meta/subscribe</c> request for one channel.</summary>
    public class SubscribeRequestModel : BayeuxRequestModel
    {
        /// <summary>The channel being subscribed to.</summary>
        [JsonPropertyName("subscription")]
        public string Subscription { get; set; }

        /// <summary>Creates a subscribe request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="subscription">The channel to subscribe to.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public SubscribeRequestModel(string? clientId, string subscription, string requestId)
        : base(clientId, BayeuxConstants.MetaChannels.Subscribe, requestId) => Subscription = subscription;
    }

    /// <summary>A <c>/meta/unsubscribe</c> request for one channel.</summary>
    public class UnsubscribeRequestModel : BayeuxRequestModel
    {
        /// <summary>The channel being unsubscribed from.</summary>
        [JsonPropertyName("subscription")]
        public string Subscription { get; set; }

        /// <summary>Creates an unsubscribe request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="subscription">The channel to unsubscribe from.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public UnsubscribeRequestModel(string? clientId, string subscription, string requestId)
        : base(clientId, BayeuxConstants.MetaChannels.Unsubscribe, requestId) => Subscription = subscription;
    }

    /// <summary>
    /// A <c>/meta/connect</c> request. The server holds this open until it has messages or its
    /// timeout elapses &#8212; this is the long poll.
    /// </summary>
    public class ConnectRequestModel : BayeuxRequestModel
    {
        /// <summary>The transport in use for this session.</summary>
        [JsonPropertyName("connectionType")]
        public string ConnectionType { get; set; }

        /// <summary>Creates a connect request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="requestId">Client-generated message id.</param>
        /// <param name="connectionType">The protocol's name for the transport in use, such as <c>long-polling</c>.</param>
        public ConnectRequestModel(string clientId, string requestId, string connectionType)
        : base(clientId, BayeuxConstants.MetaChannels.Connect, requestId) => ConnectionType = connectionType;
    }

    /// <summary>A <c>/meta/disconnect</c> request, releasing the session on the server.</summary>
    public class DisconnectRequestModel : BayeuxRequestModel
    {
        /// <summary>Creates a disconnect request.</summary>
        /// <param name="clientId">The session id to release.</param>
        /// <param name="requestId">Client-generated message id.</param>
        public DisconnectRequestModel(string clientId, string requestId)
        : base(clientId, BayeuxConstants.MetaChannels.Disconnect, requestId)
        { }
    }

    /// <summary>A message published to an application channel.</summary>
    public class PublishRequestModel : BayeuxRequestModel
    {
        /// <summary>
        /// The payload. <c>PublishAsync</c> passes it already serialized - a <see cref="JsonElement"/>
        /// written with the client's <c>JsonSerializerOptions</c> - so the protocol's own fields
        /// and the application's data each follow their own naming rules.
        /// </summary>
        [JsonPropertyName("data")]
        public object? Data { get; set; }

        /// <summary>Creates a publish request.</summary>
        /// <param name="clientId">The session id from the handshake.</param>
        /// <param name="channel">The application channel to publish to.</param>
        /// <param name="data">The payload.</param>
        /// <param name="requestId">Client-generated message id, echoed in the reply.</param>
        public PublishRequestModel(string clientId, string channel, object? data, string requestId)
        : base(clientId, channel, requestId) => Data = data;
    }
}
