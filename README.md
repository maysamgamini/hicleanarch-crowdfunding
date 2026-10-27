# hicleanarch-crowdfunding

The reference code for [hicleanarch.com](https://hicleanarch.com): a crowdfunding platform built as a cloud-native modular monolith with clean architecture, on .NET 10 and AWS.

## Each lesson is a tag

The history on `main` is linear, and every Builder lesson of the course ends at a tag named `s<stage>-l<nn>`, for example `s2-l07`. A lesson starts at the tag of the lesson before it. `s2-l00` is the empty solution.

To work through a lesson:

```bash
git checkout s2-l06          # the start of lesson s2-l07
git switch -c my-s2-l07      # make the change the lesson describes
dotnet test                  # the lesson names the tests that must pass
git diff s2-l07              # compare with the reference end state
```

## Run it

```bash
scripts/pack-buildingblocks.sh                # the shared BuildingBlocks, packed fresh for the modules to restore
docker compose up -d                          # PostgreSQL 17 on localhost:5432
dotnet run --project src/Host -- migrate      # roles, schemas and migrations, then exit (a deploy step)
dotnet run --project src/Host                 # the API on http://localhost:5080
curl -X POST localhost:5080/v1/campaigns -H 'Content-Type: application/json' \
  -d '{"title":"Harbor Lights","goal_minor":5000000,"currency":"USD","deadline":"2027-03-31T23:59:00-04:00","tiers":[]}'
dotnet test                                   # every test; the integration tests start their own PostgreSQL in Docker
```

Each module connects as its own database role (`campaigns_app`, `pledges_app`, `payments_app`, `notifications_app`), which can use only its own schema. Pledges' background jobs (the close and the totals aggregator) connect as `pledges_jobs`, with the same rights but without the two-second `transaction_timeout` that `migrate` sets on `pledges_app` and checks is enforced. `migrate` connects as the owner, creates the roles and schemas, applies each module's EF Core migrations and grants the module's role its tables.

Emails are kept in memory, not sent: each one is logged, and `GET /dev/emails` (optionally `?to=<user id>`) lists them, newest first. Both exist only on a laptop (the Development environment).

Payments runs on a fake PSP in memory. On a laptop, `POST /dev/fake-psp/setups/{setup_id}/complete` plays the backer finishing the card form: the setup id is the start of the `client_secret` a new-card pledge returns.

## Layout

- `src/Host`: the composition root. It only composes modules: each module brings its own services and endpoints (`AddCampaignsModule`, `MapCampaignsEndpoints`), so a module split off later gets a host of its own that calls the same two.
- `src/Modules`: Campaigns, Pledges, Payments, Notifications, each with its own layers.
- `src/BuildingBlocks` (its own solution, `BuildingBlocks.slnx`, versioned and published as NuGet packages; the modules reference a pinned version): what every module shares: the result type, the dispatcher, the HTTP error mapping.
- `tests/`: architecture rules, unit tests per module, integration tests against a real database.
- `infra/bootstrap/`: long-lived AWS resources (Terraform state bucket, ECR, CodeArtifact). Applied once and kept. Its own state is local; keep the `terraform.tfstate` file safe.
- `infra/env/`: the sandbox environment. Applied for a lesson or a test run, destroyed afterwards.
- `infra/modules/`: Terraform modules used by `infra/env/`.
- `scripts/replay.sh`: re-applies the lesson commits after an earlier lesson changes, and moves the tags.

## Requirements

.NET SDK 10, Docker, Terraform 1.11 or later (S3 state locking). An AWS account only for the lessons that deploy.
