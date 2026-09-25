// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using Bayeux.Client.Extensible.Core.Models;

namespace Bayeux.Client.Extensible.Interfaces
{
    /// <summary>
    /// Reads and writes the <c>ext</c> field of Bayeux messages - the protocol's own extension
    /// point, used by acknowledgement, time sync, Salesforce replay and ext-based authentication.
    /// </summary>
    public interface IBayeuxExt
    {
        /// <summary>
        /// Called for every outgoing message, before it is serialised. May add to
        /// <see cref="BaseLongPollingRequestModel.Ext"/>. Asynchronous because an extension may
        /// have to obtain something before the message can go out - a fresh token for ext-based
        /// authentication being the usual case. Synchronous work returns
        /// <see cref="Task.CompletedTask"/>, which allocates nothing.
        /// </summary>
        /// <param name="requestModel">The message about to be sent. Its <c>Ext</c> may be written.</param>
        /// <param name="cancellationToken">
        /// The token of the operation sending this message: the session's own for the long poll, the
        /// caller's for connect, subscribe and unsubscribe, the disconnect timeout for the goodbye.
        /// Pass it to anything the extension awaits, so that stopping the poller or giving up on a
        /// call is not held up by an extension's I/O. A cancellation it causes is not an extension
        /// failure.
        /// </param>
        /// <returns>A task that completes when the message is ready to be sent.</returns>
        /// <remarks>
        /// Called once per message, not once per HTTP request: five subscriptions in one batch are
        /// five calls. Throwing stops the message from being sent and fails the operation.
        /// </remarks>
        Task OutgoingAsync(BaseLongPollingRequestModel requestModel, CancellationToken cancellationToken);

        /// <summary>
        /// Called for every incoming message, before the poller acts on it and before any handler
        /// sees it. Synchronous on purpose: it records what arrived, and awaiting here would delay
        /// delivery of every event behind the extension's I/O.
        /// </summary>
        /// <param name="responseModel">The message the server sent. Its <c>Ext</c> may be read.</param>
        void Incoming(BayeuxResponseMessageModel responseModel);
    }
}