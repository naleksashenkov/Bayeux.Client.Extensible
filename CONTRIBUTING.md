# Contributing

Thanks for your interest in improving Bayeux.Client.Extensible.

## Developer Certificate of Origin

Every contribution to this project must be made under the [Developer Certificate of Origin](DCO) (DCO) version 1.1, the full text of which is in the [`DCO`](DCO) file at the repository root.

You certify the DCO by adding a `Signed-off-by` line to each commit:

```
Signed-off-by: Jane Developer <jane@example.com>
```

Git adds this line for you when you commit with `-s`:

```bash
git commit -s -m "Add reconnect backoff to the long-polling transport"
```

The name and email must be your real ones and must match your Git author identity — pseudonymous sign-offs cannot be accepted. To set them once:

```bash
git config user.name "Jane Developer"
git config user.email "jane@example.com"
```

**Every commit in a pull request must be signed off.** If you forget, amend the last commit with `git commit -s --amend --no-edit`, or sign off a whole branch with `git rebase --signoff main`, then force-push.

By signing off you are stating that you wrote the contribution or otherwise have the right to submit it under the Apache License 2.0. You are not assigning copyright; you keep it.

## Licence

Contributions are accepted under the [Apache License, Version 2.0](LICENSE). Add this header to every new source file:

```csharp
// Copyright (c) 2026 Nikita Aleksashenkov
// Licensed under the Apache License, Version 2.0.
// See LICENSE in the repository root for full license information.
```

## Before you open a pull request

- **Protocol changes must cite the specification.** This client is written from the Bayeux protocol specification. If you change message handling, reference the relevant section rather than another implementation's behaviour.
- **Do not copy code from other Bayeux clients.** This project is a clean-room implementation and must stay that way. Contributions containing code derived from other implementations cannot be accepted, regardless of that project's licence.
- Keep public API changes additive where possible, and document them with XML doc comments (`/// <summary>`).
- Add or update tests for behaviour you change.
- Run `dotnet build` and `dotnet test` before submitting.

## Reporting bugs

Open an issue describing what you expected, what happened, the transport and authentication strategy in use, and a minimal reproduction. Please redact tokens, credentials and endpoint hostnames.

## Security

Do not report security issues in public issues. See the repository's security policy, or contact the maintainer directly.

## Code of conduct

Participation in this project is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
