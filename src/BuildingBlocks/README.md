# CrowdFunding.BuildingBlocks

What every module of the crowdfunding platform shares, as three packages:

- `CrowdFunding.BuildingBlocks.Domain`: `Result<T>`, `Error`, public ids.
- `CrowdFunding.BuildingBlocks.Application`: commands, queries, handlers, validators and the dispatcher;
  `ContractJson`, the strict options every message between modules is read with, and `RemoteEndpoint`,
  which names the HTTP endpoint of a command another service may send.
- `CrowdFunding.BuildingBlocks.Infrastructure`: problem replies, idempotent requests, the outbox and inbox, the database migrator, authentication, and the service host builder (`AddServiceDefaults`, `UseServiceDefaults`, `RunService`) every service host is made with (`templates/service`).

Versions follow SemVer. A change that can break a caller (a removed or renamed public type or member,
a changed signature or behaviour a caller relies on) is a new major version. A new public type or
member is a minor version. A fix is a patch. Each module pins the version it was built and tested with,
and upgrades on its own schedule.

Build and pack locally:

```bash
dotnet pack BuildingBlocks.slnx -o artifacts/packages
```

Publish: push a tag `bb-v<version>` matching the version in `src/BuildingBlocks/Directory.Build.props`;
the `package` workflow packs and publishes to AWS CodeArtifact.

## Versions

- **6.0.0**: `ITokenBuckets` gains `TryEnter` and the `Lane` record (a line of waiting callers);
  `IInFlightSlots` caps calls in flight beside the buckets; `ValkeyTokenBuckets` takes a
  `DegradedTokenBuckets`, used while Valkey cannot be reached; `ValkeyRegistration.ClientOptions`
  sets short client timeouts; the query cache serves a stale entry while one caller reloads it;
  `IdempotentRequests.TryReplay` replays a stored reply without a transaction; `PurgeConnection`
  points a module's key purge at another connection. The first three break an implementer, so
  this is a major version.
