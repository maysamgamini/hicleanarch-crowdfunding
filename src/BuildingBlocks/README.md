# CrowdFunding.BuildingBlocks

What every module of the crowdfunding platform shares, as three packages:

- `CrowdFunding.BuildingBlocks.Domain`: `Result<T>`, `Error`, public ids.
- `CrowdFunding.BuildingBlocks.Application`: commands, queries, handlers, validators and the dispatcher.
- `CrowdFunding.BuildingBlocks.Infrastructure`: problem replies, idempotent requests, the outbox and inbox, the database migrator, authentication.

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
