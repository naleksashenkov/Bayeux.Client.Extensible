// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bayeux.Client.Extensible.Core
{
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

        /// <summary>
        /// In a handshake reply, the transports the server offers. Checked against the client's own
        /// before anything else is sent.
        /// </summary>
        [JsonPropertyName("supportedConnectionTypes")]
        public string?[]? SupportedConnectionTypes { get; set; }

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
        /// it means cookie state is being shared between clients that should be independent.
        /// </summary>
        [JsonPropertyName("multiple-clients")]
        public bool? IsMultipleClients { get; set; }
    }
}
