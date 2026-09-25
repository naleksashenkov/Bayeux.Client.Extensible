// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

namespace Bayeux.Client.Extensible.Core.Constants
{
    /// <summary>
    /// Names fixed by the Bayeux protocol: meta channels and <c>advice.reconnect</c> values.
    /// </summary>
    /// <remarks>
    /// Internal on purpose: a public constant is a promise that outlives every version, and these
    /// names are fixed by the protocol anyway, so code outside the library - an
    /// <c>IBayeuxExt</c> implementation above all - spells them out itself.
    /// </remarks>
    internal static class CometDConstants
    {
        /// <summary>
        /// The Bayeux meta channels: the protocol's own control messages, as opposed to the
        /// application channels that carry events. Compared ordinally, as channel names always are.
        /// </summary>
        internal static class MetaChannels
        {
            /// <summary>Ends a session. Sent by the client on shutdown, and by the server to end one itself.</summary>
            public const string Disconnect = "/meta/disconnect";

            /// <summary>
            /// Opens a session and issues the client id. Also where extensions negotiate: a client
            /// announces one in the outgoing <c>ext</c>, the server agrees in the reply's.
            /// </summary>
            public const string Handshake = "/meta/handshake";

            /// <summary>Subscribes the session to a channel or pattern.</summary>
            public const string Subscribe = "/meta/subscribe";

            /// <summary>Removes a subscription.</summary>
            public const string Unsubscribe = "/meta/unsubscribe";

            /// <summary>
            /// The long poll: held open by the server until it has events or its timeout elapses.
            /// Its reply carries <c>advice</c>, and often <c>ext</c> as well.
            /// </summary>
            public const string Connect = "/meta/connect";

            /// <summary>
            /// Prefix shared by every meta channel. A message whose channel does not start with it
            /// is an application event.
            /// </summary>
            public const string Meta = "/meta/";
        }

        /// <summary>
        /// Values of <c>advice.reconnect</c>: what the server tells the client to do next. The third,
        /// <c>retry</c>, is also the default when the field is absent, and needs no constant.
        /// </summary>
        internal static class Reconnect
        {
            /// <summary>Stop: do not reconnect and do not handshake again.</summary>
            public const string None = "none";

            /// <summary>The session is gone: handshake again to open a new one.</summary>
            public const string Handshake = "handshake";
        }
    }
}
