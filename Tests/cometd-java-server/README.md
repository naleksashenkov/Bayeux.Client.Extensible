# CometD reference server (Java)

The integration tests run the client against the CometD reference implementation. The
[Node.js port](../cometd-server) has no WebSocket transport; this one, the Java original, has
both. Test infrastructure only - nothing here is part of the library or its package.

| Path | Transports |
|---|---|
| `/cometd` | long-polling and WebSocket |
| `/cometd-lp` | long-polling only - a WebSocket upgrade is refused with `400` |
| `/control/publish?channel=…&text=…` | publishes to a channel of `/cometd` |
| `/control/kill?clientId=…` | ends a session of `/cometd` from the server's side |

Acknowledgements and timesync are on, for clients that ask for them. Timeouts are short, as in the Node.js server: a `/meta/connect` is held for 2 seconds, and a
session expires 4 seconds after its last one. CometD 8 on Jetty 12; needs Java 17 or later.

## Run

```bash
cd Tests/cometd-java-server
mvn -q package dependency:copy-dependencies -DincludeScope=runtime
java -cp "target/classes:target/dependency/*" bayeux.testserver.TestServer
```

It listens on `http://127.0.0.1:8098/`; a port given as the first argument replaces that. On
Windows, separate the class path with `;` instead of `:`.

Then, from the repository root, with the Node.js server on its default port as well:

```bash
BAYEUX_COMETD_URL=http://127.0.0.1:8099/ BAYEUX_COMETD_JAVA_URL=http://127.0.0.1:8098/ \
  dotnet test Tests/Bayeux.Client.Extensible.IntegrationTests
```

Each test runs once per server and transport: Node.js over long-polling, Java over long-polling,
Java over WebSocket. A server whose variable is not set has its tests skipped.
