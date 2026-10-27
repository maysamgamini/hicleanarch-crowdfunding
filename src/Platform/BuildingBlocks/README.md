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

Publish: push a tag `bb-v<version>` matching the version in `src/Platform/BuildingBlocks/Directory.Build.props`;
the `package` workflow packs and publishes to AWS CodeArtifact.

## Versions

- **6.3.0**: `ServiceVersion` (lesson s5-l14): `UseServiceDefaults` maps `GET /version`, which answers
  the service's name and the release it runs (`SERVICE_VERSION`, set by the service module to the
  image tag; `dev` when unset).
- **6.2.0**: `JourneyLog` (lesson s5-l13): one structured line per journey step, with `campaign.id` and
  `pledge.id`, written by `OutboxCommitInterceptor` for each committed event and by `OutboxDispatcher`
  for each handler it runs (handled or failed). `OutboxCommitInterceptor` gains a constructor that
  takes the logger; the old one stays and writes no lines.
- **6.1.0**: `RetiredSettings`: `AddServiceDefaults` refuses to start a program that still sets any
  `Modules:*` key (lesson s5-l10: every module runs in its own service).
- **6.0.1**: `DatabaseBusy.WithLockTimeout` puts the lock wait back to the session's own once its
  statement is done, so it no longer lasts the whole transaction (CQA-1580, 5dd059e). That commit
  changed the code and kept 6.0.0, so the version is bumped here (CQA-2112), and CI now refuses
  a BuildingBlocks change that keeps its version (`scripts/check-buildingblocks-version.sh`).
- **6.0.0**: `ITokenBuckets` gains `TryEnter` and the `Lane` record (a line of waiting callers);
  `IInFlightSlots` caps calls in flight beside the buckets; `ValkeyTokenBuckets` takes a
  `DegradedTokenBuckets`, used while Valkey cannot be reached; `ValkeyRegistration.ClientOptions`
  sets short client timeouts; the query cache serves a stale entry while one caller reloads it;
  `IdempotentRequests.TryReplay` replays a stored reply without a transaction; `PurgeConnection`
  points a module's key purge at another connection. The first three break an implementer, so
  this is a major version.
