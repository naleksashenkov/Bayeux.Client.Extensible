# Security policy

## Supported versions

| Version | Supported |
|---|---|
| The latest `1.0.0` prerelease (`-alpha`, `-beta`, `-rc`) | Yes |
| Earlier prereleases | No — update to the latest |

Once 1.0 is out, the latest 1.x release is supported. Fixes are released as a new version; a
published version is never replaced.

## Reporting a vulnerability

**Please report vulnerabilities privately**, not in a public issue: use **Report a vulnerability**
on the repository's [Security tab](https://github.com/naleksashenkov/Bayeux.Client.Extensible/security).
That gives time to release a fix before the details are public, and lets the fix be announced as a
GitHub security advisory — which alerts, through Dependabot, everyone who depends on the package.

Include, as far as you can:

- the version and target framework, the transport (long-polling, WebSocket, `Auto`) and the
  authentication provider in use;
- what an attacker can do, and under which conditions;
- a minimal reproduction, against a local or test server.

Never include real credentials, tokens, session cookies or internal hostnames.

## What to expect

- An acknowledgement within a few days.
- An assessment, and a plan for the fix, as soon as the issue is understood.
- A fixed version and a published advisory, crediting you unless you prefer otherwise.

## What counts as a vulnerability

Reports are welcome for anything that lets the library be used against the application that hosts
it, for example:

- credentials, tokens or cookies leaking — into logs, exceptions, events or another session;
- one client's session or cookies reaching another client sharing the same `HttpClient`;
- a server's response, or a man in the middle, making the client run code, exhaust memory or stop
  for good when it should recover.

Ordinary bugs, missing features and questions belong in
[issues](https://github.com/naleksashenkov/Bayeux.Client.Extensible/issues).
