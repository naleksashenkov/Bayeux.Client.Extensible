// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using Bayeux.Client.Extensible.Authentication;

namespace Bayeux.Client.Extensible.Core
{
    /// <summary>
    /// Bayeux over one WebSocket: every batch is a text frame, replies come back in frames of their
    /// own, and the server pushes events the moment it has them instead of holding them for the next
    /// <c>/meta/connect</c> reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reply is not tied to its request the way an HTTP response is, so it is matched by the
    /// message <c>id</c>; whatever no request waits for - events, a message the server sends on its
    /// own, a reply that arrived after its caller gave up - goes to the client's callback. Meaning is
    /// still the client's business: this class only sorts by <c>id</c>.
    /// </para>
    /// <para>
    /// The socket is opened on the first send and again after it dies, never reused: a
    /// <see cref="ClientWebSocket"/> cannot reconnect. The session outlives it - CometD does not bind
    /// a session to a socket - so the client simply carries on with the same id over the new one.
    /// </para>
    /// <para>
    /// Credentials and cookies go out once, in the upgrade request. New credentials from
    /// <c>UpdateCredentials</c> reach the server only with the next socket.
    /// </para>
    /// </remarks>
    internal sealed class WebSocketTransport : IBayeuxTransport
    {
        // Where the socket connects: the endpoint with ws or wss in place of http or https.
        private readonly Uri _address;

        // The same endpoint as http(s), for the auth provider: one that looks at the scheme or the
        // host sees what it would see with long-polling.
        private readonly Uri _httpAddress;

        private readonly IAuthProvider _authProvider;

        // The client's, as with long-polling: sent with the upgrade request, and Set-Cookie from its
        // response is stored back.
        private readonly CookieContainer _cookieContainer;

        // Where messages no request waits for go. Called on the receive loop.
        private readonly Action<BayeuxResponseMessageModel[]> _onPushed;

        // A WebSocket allows one send at a time. Also keeps two callers from opening two sockets,
        // and orders disposal against sending.
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // The transport's lifetime. The only token socket operations get: cancelling a send or a
        // receive aborts the whole socket, so a caller's token must never reach them.
        private readonly CancellationTokenSource _cts = new();

        // The current socket, or the last one, possibly dead. Replaced only under _sendLock.
        private WebSocketConnection? _connection;

        // Read and written only under _sendLock: a send that gets the lock after disposal must not
        // open a socket nobody will close.
        private bool _isDisposed;

        // Makes DisposeAsync run once: the client disposes the transport both at the end of the loop
        // and in its own DisposeAsync.
        private int _disposalStarted;

        /// <inheritdoc/>
        public string ConnectionType => "websocket";

        /// <inheritdoc/>
        /// <remarks>
        /// Always <c>null</c>: there is no HTTP request to time out. A held <c>/meta/connect</c> waits
        /// until its reply arrives or the socket fails.
        /// </remarks>
        public TimeSpan? RequestTimeout => null;

        /// <summary>Creates the transport. Nothing is opened until the first send.</summary>
        /// <param name="baseAddress">The HttpClient's base address, <c>http</c> or <c>https</c>.</param>
        /// <param name="authProvider">Applied to the upgrade request of every socket.</param>
        /// <param name="path">The endpoint path, relative to <paramref name="baseAddress"/>.</param>
        /// <param name="cookieContainer">The client's cookie store.</param>
        /// <param name="onPushed">
        /// Receives every message no request waits for. Called on the receive loop, which waits for it:
        /// it must return quickly and must not wait for a reply itself.
        /// </param>
        public WebSocketTransport(
            Uri baseAddress,
            IAuthProvider authProvider,
            string path,
            CookieContainer cookieContainer,
            Action<BayeuxResponseMessageModel[]> onPushed)
        {
            _httpAddress = new Uri(baseAddress, path);

            var builder = new UriBuilder(_httpAddress)
            {
                Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
            };

            _address = builder.Uri;
            _authProvider = authProvider;
            _cookieContainer = cookieContainer;
            _onPushed = onPushed;
        }

        /// <summary>Closes the socket and waits for its receive loop to end. Safe to call more than once.</summary>
        /// <remarks>
        /// <para>
        /// Closes politely first - a Close frame the server answers with its own, which ends the
        /// receive loop - and gives up on that after a second. Cancelling the lifetime token then
        /// stops the loop whatever the server did.
        /// </para>
        /// <para>
        /// The Close frame is a send, so it goes out under the send lock; a send stuck holding the
        /// lock costs the polite close, not the disposal.
        /// </para>
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposalStarted, 1) > 0)
                return;

            WebSocketConnection? connection;

            var locked = await _sendLock.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            try
            {
                _isDisposed = true;
                connection = _connection;

                if (locked && connection is { IsOpen: true })
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        await connection.Socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            null,
                            timeoutCts.Token)
                        .ConfigureAwait(false);
                    }
                    catch { }   // the socket is already dead, or the server is slow: closed below either way
                }
            }
            finally
            {
                if (locked)
                    _sendLock.Release();
            }

            _cts.Cancel();

            // Never throws: the loop catches everything itself.
            if (connection != null)
                await connection.ReceiveLoop.ConfigureAwait(false);

            _cts.Dispose();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Sends the batch as one frame and waits for a reply to every message in it. Each message is
        /// registered before the frame goes out, because a fast server may answer before the send
        /// returns.
        /// </para>
        /// <para>
        /// <paramref name="cancellationToken"/> cancels only the wait: the socket and every other
        /// request on it carry on. A reply that comes after all goes to the push callback.
        /// </para>
        /// <para>
        /// Replies are returned in the order of the request, not of arrival. Events never are: they
        /// carry no request's id, and go to the push callback.
        /// </para>
        /// <para>
        /// A dead connection is found here, by the reply that does not come: a send into one still
        /// succeeds, since it only fills a local buffer. Past <paramref name="timeout"/> the socket is
        /// aborted - every request on it fails, this one included - and the next send opens a new
        /// one. The timer starts after the send, outside the lock, so a slow reply holds up nobody.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="messages"/> is null or empty.</exception>
        /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
        /// <exception cref="WebSocketException">
        /// The socket could not be opened, failed before every reply came, or no reply came within
        /// <paramref name="timeout"/>.
        /// </exception>
        public async Task<BayeuxResponseMessageModel[]> SendAsync(
            BayeuxRequestModel[] messages, 
            CancellationToken cancellationToken, 
            TimeSpan? timeout)
        {
            if (messages == null || messages.Length == 0)
                throw new ArgumentException("Messages are null or empty");

            WebSocketConnection connection;
            var waits = new Task<BayeuxResponseMessageModel>[messages.Length];

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_isDisposed)
                    throw new ObjectDisposedException(nameof(WebSocketTransport));

                connection = _connection is { IsOpen: true } openConnection
                    ? openConnection
                    : _connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < messages.Length; i++)
                    waits[i] = connection.Register(messages[i].RequestId);

                var bytes = Encoding.UTF8.GetBytes(BayeuxRequestModel.FormRequestJson(messages));

                try
                {
                    // The lifetime token, not the caller's: cancelling a send aborts the socket.
                    await connection.Socket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        _cts.Token)
                    .ConfigureAwait(false);
                }
                catch
                {
                    // Nobody will await these: remove them now, or the receive loop would fail them
                    // later into unobserved exceptions.
                    connection.Cancel(messages, CancellationToken.None);
                    throw;
                }
            }
            finally
            {
                _sendLock.Release();
            }

            var allWaits = Task.WhenAll(waits);

            using (cancellationToken.Register(() => connection.Cancel(messages, cancellationToken)))
            {
                if (timeout is { } limit)
                {
                    using var timeoutCts = new CancellationTokenSource();

                    // The reply or the limit, whichever is first; the timer is stopped either way.
                    if (await Task.WhenAny(allWaits, Task.Delay(limit, timeoutCts.Token)).ConfigureAwait(false) != allWaits)
                        connection.Abort(new WebSocketException(
                            WebSocketError.Faulted, 
                            $"Connection is presumed dead because of no reply within {limit}"));

                    timeoutCts.Cancel();
                }
                return await allWaits.ConfigureAwait(false);
            }
        }

        /// <summary>Opens a new socket, with the provider's credentials and the client's cookies.</summary>
        /// <param name="cancellationToken">Cancels the upgrade. Safe here: there is no socket yet to abort.</param>
        /// <returns>The connection, its receive loop already running.</returns>
        /// <remarks>
        /// <see cref="IAuthProvider"/> decorates an <see cref="HttpRequestMessage"/>, so it is given a
        /// blank one and its headers are copied onto the upgrade request. Anything else it sets on
        /// the message is lost - every provider in this library sets headers only.
        /// </remarks>
        private async Task<WebSocketConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.Cookies = _cookieContainer;

            using (var blank = new HttpRequestMessage(HttpMethod.Get, _httpAddress))
            {
                await _authProvider.ApplyAsync(blank, cancellationToken).ConfigureAwait(false);

                foreach (var header in blank.Headers)
                    socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            }

            try
            {
                await socket.ConnectAsync(_address, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            return new WebSocketConnection(socket, _onPushed, _cts.Token);
        }
    }
}
