import http from 'http';
import * as cometd from 'cometd-nodejs-server';

// Short timeouts so the test finishes quickly.
const server = cometd.createCometDServer({
    timeout: 2000,            // how long /meta/connect is held
    maxInterval: 4000,        // session expiry
    logLevel: 'info'
});

const stats = { handshakes: 0, subscribes: 0, disconnects: 0, sessions: [] };

server.addListener('sessionAdded', (session) => {
    stats.handshakes++;
    stats.sessions.push(session.id);
    console.log('[server] session added:', session.id);
});

server.addListener('sessionRemoved', (session, timeout) => {
    stats.disconnects++;
    console.log('[server] session removed:', session.id, 'timeout:', timeout);
});

const httpServer = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');

    // Control plane for the test driver. Wrapped, because a throw here takes the whole process
    // down and every later test then fails with "connection refused" instead of its own result.
    if (url.pathname.startsWith('/control/')) {
        const action = url.pathname.slice('/control/'.length);

        try {
            if (action === 'publish') {
                const channel = url.searchParams.get('channel');
                const text = url.searchParams.get('text') ?? 'hello';

                // CometD drops a channel once its last subscriber leaves, so getServerChannel
                // returns undefined when a test publishes after unsubscribing - which is exactly
                // what a test checking that delivery stopped needs to do.
                const target = server.getServerChannel(channel) ?? server.createServerChannel(channel);

                target.publish(null, { text, at: Date.now() });
                console.log('[server] published to', channel);
            } else if (action === 'kill') {
                // Force a session away so its next /meta/connect gets 402::Unknown client.
                // Tests name their own session; several may be alive at once.
                const id = url.searchParams.get('clientId') ?? stats.sessions[stats.sessions.length - 1];
                const s = id ? server.getServerSession(id) : null;
                if (s) { s.disconnect(); console.log('[server] killed session', id); }
                else { console.log('[server] no session to kill for', id); }
            }
        } catch (e) {
            console.log('[server] control failed:', action, e.message);
            res.writeHead(500, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: e.message }));
            return;
        }

        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(stats));
        return;
    }

    if (url.pathname.startsWith('/cometd')) {
        server.handle(req, res);
        return;
    }

    res.writeHead(404);
    res.end();
});

httpServer.listen(8099, '127.0.0.1', () => console.log('[server] listening on http://127.0.0.1:8099/'));
