// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// One socket of a <see cref="WebSocketTransport"/>, and everything that lives exactly as long:
    /// the requests waiting for a reply on it and the loop that reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept apart from the transport so that a dead socket takes only its own requests with it.
    /// With one table for the whole transport, a socket failing would also fail a request already
    /// sent over its replacement.
    /// </para>
    /// <para>
    /// The receive loop owns the socket: whatever ends it, it fails what is still waiting and
    /// disposes the socket. Nobody else closes it, and replacing a connection needs no cleanup.
    /// </para>
    /// </remarks>
    internal sealed class WebSocketConnection
    {
        // Requests sent on this socket that have no reply yet, by message id.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BayeuxResponseMessageModel>> _pendingRequests = new();

        private readonly Action<BayeuxResponseMessageModel[]> _onPushed;

        // Cleared by the receive loop when it ends, before it fails what is waiting; Register reads
        // it after adding, so no request can slip in between and wait forever.
        private volatile bool _isOpen = true;

        // Why Abort was called, reported to every waiting request instead of whatever the
        // interrupted receive throws - which may be a bare cancellation. The first reason wins.
        private Exception? _abortReason;

        /// <summary>The socket. Sent on by the transport under its lock; read only by the receive loop.</summary>
        public WebSocket Socket { get; }

        /// <summary>
        /// Whether the socket can still be sent on. Checks the socket's own state as well: it may be
        /// aborted before the receive loop notices.
        /// </summary>
        public bool IsOpen => _isOpen && Socket.State == WebSocketState.Open;

        /// <summary>The receive loop. Never faults; completes once the socket is disposed.</summary>
        public Task ReceiveLoop { get; }

        /// <summary>Takes over an open socket and starts reading it.</summary>
        /// <param name="socket">The connected socket. Disposed by this connection.</param>
        /// <param name="onPushed">Receives every message no request waits for.</param>
        /// <param name="cancellationToken">The transport's lifetime: cancelling it ends the loop and aborts the socket.</param>
        public WebSocketConnection(WebSocket socket, Action<BayeuxResponseMessageModel[]> onPushed, CancellationToken cancellationToken)
        {
            Socket = socket;
            _onPushed = onPushed;
            ReceiveLoop = Task.Run(() => ReceiveLoopAsync(cancellationToken));
        }

        /// <summary>Starts waiting for the reply to one message. Call before the message is sent.</summary>
        /// <param name="messageId">The message's <c>id</c>, which the server echoes in its reply.</param>
        /// <returns>
        /// The reply. Fails if the socket closes first; cancelled through <see cref="Cancel"/>.
        /// </returns>
        /// <remarks>
        /// Continuations run asynchronously: completing the task on the receive loop must not run the
        /// waiting caller's code there, or reading would stop until that code finished - and stop for
        /// good if it waited for another reply.
        /// </remarks>
        public Task<BayeuxResponseMessageModel> Register(string messageId)
        {
            var wait = new TaskCompletionSource<BayeuxResponseMessageModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[messageId] = wait;

            // The loop may have ended between the transport's IsOpen check and the line above; its
            // final sweep would then have missed this entry.
            if (!_isOpen && _pendingRequests.TryRemove(messageId, out _))
                wait.TrySetException(new WebSocketException("WebSocket is closed"));

            return wait.Task;
        }

        /// <summary>Stops waiting for the replies to these messages. The socket is not touched.</summary>
        /// <param name="requestMessages">The messages whose waits to cancel.</param>
        /// <param name="cancellationToken">The token reported by the cancelled tasks.</param>
        /// <remarks>A reply that still arrives goes to the push callback, as one nobody waits for.</remarks>
        public void Cancel(BayeuxRequestModel[] requestMessages, CancellationToken cancellationToken)
        {
            foreach (var message in requestMessages)
            {
                if (_pendingRequests.TryRemove(message.RequestId, out var wait))
                    wait.TrySetCanceled(cancellationToken);
            }
        }

        /// <summary>
        /// Drops the socket at once, failing every request still waiting on it with
        /// <paramref name="abortReason"/>.
        /// </summary>
        /// <param name="abortReason">What the waiting requests are told.</param>
        /// <remarks>
        /// Safe while the receive loop reads and the transport sends: that is what
        /// <see cref="WebSocket.Abort"/> is for. It wakes the pending receive, and the loop's
        /// <c>finally</c> does the rest. <see cref="IsOpen"/> turns false at once, so the next send
        /// opens a new socket even before the loop has ended.
        /// </remarks>
        public void Abort(Exception abortReason)
        {
            Interlocked.CompareExchange(ref _abortReason, abortReason, null);
            Socket.Abort();
        }

        /// <summary>
        /// Reads the socket until it fails or closes, hands each reply to its waiting request and
        /// everything else to the push callback.
        /// </summary>
        /// <param name="cancellationToken">The transport's lifetime.</param>
        /// <remarks>
        /// <para>
        /// The only reader of the socket, as a WebSocket requires. A message may arrive in several
        /// fragments; they are collected until <c>EndOfMessage</c>.
        /// </para>
        /// <para>
        /// Never waits for anything but the socket. The push callback only queues, and an exception
        /// from it is swallowed: a bug in an extension must not take the socket down. A frame that is
        /// not valid JSON does end the loop - the stream can no longer be trusted - and the next
        /// send opens a new socket.
        /// </para>
        /// <para>
        /// A Close frame from the server ends it too, as a failure: requests still waiting will never
        /// be answered on this socket.
        /// </para>
        /// </remarks>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            Exception? error = null;
            var buffer = new byte[16 * 1024];

            try
            {
                while (true)
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The server closed the socket.");

                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var messages = JsonSerializer.Deserialize<BayeuxResponseMessageModel[]>(
                        new ReadOnlySpan<byte>(stream.GetBuffer(), 0, (int)stream.Length)) ?? [];

                    List<BayeuxResponseMessageModel>? pushed = null;

                    foreach (var message in messages)
                    {
                        if (message.Id is { } messageId && _pendingRequests.TryRemove(messageId, out var wait))
                            wait.TrySetResult(message);
                        else
                            (pushed ??= []).Add(message);
                    }

                    if (pushed != null)
                    {
                        try
                        {
                            _onPushed(pushed.ToArray());
                        }
                        catch { }   // the client reports its own errors; the socket must survive them
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // The flag first, then the sweep: see Register.
                _isOpen = false;
                foreach (var messageId in _pendingRequests.Keys)
                    if (_pendingRequests.TryRemove(messageId, out var wait))
                        wait.TrySetException(Volatile.Read(ref _abortReason) ?? error ?? new WebSocketException("Socket is closed"));

                Socket.Abort();
                Socket.Dispose();
            }
        }
    }
}
