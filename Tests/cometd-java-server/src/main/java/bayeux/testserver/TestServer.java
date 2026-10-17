// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.

package bayeux.testserver;

import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

import org.cometd.bayeux.Promise;
import org.cometd.bayeux.server.BayeuxServer;
import org.cometd.bayeux.server.ServerMessage;
import org.cometd.bayeux.server.ServerSession;
import org.cometd.server.ext.AcknowledgedMessagesExtension;
import org.cometd.server.ext.TimesyncExtension;
import org.cometd.server.http.JSONHttpTransport;
import org.cometd.server.http.jetty.CometDHandler;
import org.cometd.server.websocket.jetty.JettyWebSocketTransport;
import org.eclipse.jetty.http.HttpHeader;
import org.eclipse.jetty.server.Handler;
import org.eclipse.jetty.server.Request;
import org.eclipse.jetty.server.Response;
import org.eclipse.jetty.server.Server;
import org.eclipse.jetty.server.ServerConnector;
import org.eclipse.jetty.server.handler.ContextHandler;
import org.eclipse.jetty.server.handler.ContextHandlerCollection;
import org.eclipse.jetty.util.Callback;
import org.eclipse.jetty.util.Fields;
import org.eclipse.jetty.websocket.server.WebSocketUpgradeHandler;

/**
 * The CometD reference server in Java, for the integration tests: what the Node.js server in
 * {@code Tests/cometd-server} does, plus WebSocket, which the Node.js port does not have.
 *
 * <ul>
 *   <li>{@code /cometd} - long-polling and WebSocket.</li>
 *   <li>{@code /cometd-lp} - long-polling only: a WebSocket upgrade there is refused, as by a server
 *       or proxy without WebSocket.</li>
 *   <li>{@code /control/publish?channel=&text=} and {@code /control/kill?clientId=} - the test
 *       driver's control plane, the same as the Node.js server's. Both act on {@code /cometd}.</li>
 * </ul>
 *
 * Short timeouts, as in the Node.js server, so that the tests finish quickly.
 */
public final class TestServer {
    private static final AtomicInteger handshakes = new AtomicInteger();
    private static final AtomicInteger disconnects = new AtomicInteger();
    private static final List<String> sessions = new CopyOnWriteArrayList<>();

    public static void main(String[] args) throws Exception {
        int port = args.length > 0 ? Integer.parseInt(args[0]) : 8098;

        Server server = new Server();
        ServerConnector connector = new ServerConnector(server);
        connector.setHost("127.0.0.1");
        connector.setPort(port);
        server.addConnector(connector);

        ContextHandlerCollection contexts = new ContextHandlerCollection();
        server.setHandler(contexts);

        // Clients address the endpoint as /cometd, not /cometd/: without this, Jetty answers an
        // upgrade there with a redirect, which no WebSocket client follows.

        // Both transports. The WebSocket transport finds its upgrade handler through the context.
        ContextHandler both = new ContextHandler("/cometd");
        both.setAllowNullPathInContext(true);
        CometDHandler cometd = cometdHandler(
            JettyWebSocketTransport.class.getName() + "," + JSONHttpTransport.class.getName());
        WebSocketUpgradeHandler upgrade = WebSocketUpgradeHandler.from(server, both);
        upgrade.setHandler(cometd);
        both.setHandler(upgrade);
        contexts.addHandler(both);

        // Long-polling only: no upgrade handler, so an upgrade request is answered as plain HTTP.
        ContextHandler longPollingOnly = new ContextHandler("/cometd-lp");
        longPollingOnly.setAllowNullPathInContext(true);
        longPollingOnly.setHandler(cometdHandler(JSONHttpTransport.class.getName()));
        contexts.addHandler(longPollingOnly);

        ContextHandler control = new ContextHandler("/control");
        control.setHandler(new Control(cometd));
        contexts.addHandler(control);

        server.start();

        BayeuxServer bayeux = cometd.getBayeuxServer();

        // Acknowledgements, for clients that ask for them in the handshake. Per session, as in the
        // Node.js server: a client that does not ask sees no difference.
        bayeux.addExtension(new AcknowledgedMessagesExtension());

        // Timesync, answered to clients that send it - the server's half of BayeuxTimesyncExtension.
        bayeux.addExtension(new TimesyncExtension());

        bayeux.addListener(new BayeuxServer.SessionListener() {
            @Override
            public void sessionAdded(ServerSession session, ServerMessage message) {
                handshakes.incrementAndGet();
                sessions.add(session.getId());
                System.out.println("[server] session added: " + session.getId());
            }

            @Override
            public void sessionRemoved(ServerSession session, ServerMessage message, boolean timeout) {
                disconnects.incrementAndGet();
                System.out.println("[server] session removed: " + session.getId() + " timeout: " + timeout);
            }
        });

        System.out.println("[server] listening on http://127.0.0.1:" + port + "/");
        server.join();
    }

    private static CometDHandler cometdHandler(String transports) {
        CometDHandler handler = new CometDHandler();
        handler.setOptions(Map.of(
            "transports", transports,
            // Relative to the context: the whole of it.
            "cometdURLMapping", "/*",
            // How long /meta/connect is held, and how long a session outlives its last connect.
            "timeout", "2000",
            "maxInterval", "4000"));
        return handler;
    }

    /** The test driver's control plane. Every failure is answered, never thrown: a test should see its own error. */
    private static final class Control extends Handler.Abstract {
        private final CometDHandler cometd;

        Control(CometDHandler cometd) {
            this.cometd = cometd;
        }

        @Override
        public boolean handle(Request request, Response response, Callback callback) {
            String action = Request.getPathInContext(request).replaceFirst("^/", "");
            Fields query = Request.extractQueryParameters(request);
            BayeuxServer bayeux = cometd.getBayeuxServer();

            try {
                if (action.equals("publish")) {
                    String channel = query.getValue("channel");
                    String text = query.getValue("text") == null ? "hello" : query.getValue("text");

                    // Created if absent: CometD drops a channel once its last subscriber leaves, and
                    // a test checking that delivery stopped publishes after unsubscribing.
                    bayeux.createChannelIfAbsent(channel).getReference()
                        .publish(null, Map.of("text", text, "at", System.currentTimeMillis()), Promise.noop());
                    System.out.println("[server] published to " + channel);
                } else if (action.equals("kill")) {
                    // Force a session away so its next /meta/connect gets 402::Unknown client.
                    String id = query.getValue("clientId");
                    if (id == null && !sessions.isEmpty())
                        id = sessions.get(sessions.size() - 1);

                    ServerSession session = id == null ? null : bayeux.getSession(id);
                    if (session != null) {
                        session.disconnect();
                        System.out.println("[server] killed session " + id);
                    } else {
                        System.out.println("[server] no session to kill for " + id);
                    }
                }
            } catch (RuntimeException e) {
                System.out.println("[server] control failed: " + action + " " + e.getMessage());
                return reply(response, callback, 500, "{\"error\":\"" + e.getMessage() + "\"}");
            }

            return reply(response, callback, 200,
                "{\"handshakes\":" + handshakes.get() + ",\"disconnects\":" + disconnects.get() + "}");
        }

        private static boolean reply(Response response, Callback callback, int status, String json) {
            response.setStatus(status);
            response.getHeaders().put(HttpHeader.CONTENT_TYPE, "application/json");
            response.write(true, StandardCharsets.UTF_8.encode(json), callback);
            return true;
        }
    }
}
