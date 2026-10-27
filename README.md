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

Since s4-l10 the monolith no longer runs Payments, since s5-l04 it no longer runs Notifications, and since s5-l09 it no longer runs Campaigns; since s5-l10 there is no monolith: its host runs Pledges alone, as the Pledges service (`src/Services/Pledges/Pledges.Host`). A service started alone refuses to start, because no route reaches the modules that consume its events. So the way to run the system on a laptop is the local stack below: every host, the gateway and the broker, in Docker (CQA-2107). Up to s4-l09, `dotnet run --project src/Host` started the whole system in one process.

```bash
scripts/pack-contracts.sh                     # the BuildingBlocks and each module's Contracts, packed fresh for the modules to restore
scripts/local-up.sh                           # PostgreSQL, Valkey, Moto, every migrate step, every host and the gateway on http://localhost:8080
TOKEN=$(docker compose --profile stack run --rm -T -e ASPNETCORE_ENVIRONMENT=Development api token creator)   # a local creator's token
# a deadline 30 days from now: a campaign's deadline is at most 60 days away (CQA-014); BSD date, then GNU date
DEADLINE=$(date -u -v+30d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d '+30 days' +%Y-%m-%dT%H:%M:%SZ)
curl -X POST localhost:8080/v1/campaigns -H 'Content-Type: application/json' -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"title":"Harbor Lights","goal_minor":5000000,"currency":"USD","deadline":"'"$DEADLINE"'","tiers":[]}'
scripts/local-smoke.sh                        # one campaign from draft to settled, through the gateway
dotnet test                                   # every test; the integration tests start their own PostgreSQL in Docker
```

Each module connects as its own database role (`campaigns_app`, `pledges_app`, `payments_app`, `notifications_app`), which can use only its own schema. Payments' role is on Payments' own database, and each host's `migrate` step creates the roles and schemas of the modules it runs. Pledges' background jobs (the close and the totals aggregator) connect as `pledges_jobs`, with the same rights but without the two-second `transaction_timeout` that `migrate` sets on `pledges_app` and checks is enforced. `migrate` connects as the owner, creates the roles and schemas, applies each module's EF Core migrations and grants the module's role its tables.

Emails are not sent: each one is logged and kept as a row in `notifications.sent_notifications`.

Payments runs on a fake PSP in memory. In the Development environment, Payments' own host (`dotnet run --project src/Services/Payments/Payments.Host`) maps `POST /dev/fake-psp/setups/{setup_id}/complete`, which plays the backer finishing the card form; the setup id is the start of the `client_secret` a new-card pledge returns. The local stack runs as Production and has no such endpoint: `scripts/local-smoke.sh` plays the PSP instead, posting its signed webhook to the gateway.

## Run it locally

The whole system as it runs in ECS, on a laptop, with Docker only and no AWS account: PostgreSQL 17, Valkey 8 with a user per client and no default user, Moto for SNS and SQS, each service's one-time hand-over and migrate step, the four services (Pledges, Payments, Notifications, Campaigns) and the YARP gateway in front of them.

```bash
scripts/local-up.sh                           # build, provision the topics and queues, migrate, start; checks no endpoint is on AWS
scripts/local-smoke.sh                        # draft → publish → pledge → card → close → charge → settle, through the gateway
docker compose --profile stack down -v        # remove it all, data included
```

- `local-up.sh` provisions the broker on Moto from `events/broker-subscriptions.json`, the file Terraform reads (`local/broker/provision.py`): a topic per producing module, a queue and dead-letter queues per service, the filtered subscriptions. It writes their addresses to `.local/broker.env` for the hosts, as Terraform writes them into the task definitions.
- The hosts get the environment the task definitions give them, with two local substitutes: tokens come from the local issuer instead of Cognito (`docker compose run --rm -e ASPNETCORE_ENVIRONMENT=Development api token creator` prints one), and every AWS client is pointed at Moto (`AWS_ENDPOINT_URL`). Emails stay in memory; each one sent is a row in `notifications.sent_notifications`.
- The smoke test plays the PSP itself: it signs the card form's webhook with the development secret and posts it to the gateway, as the PSP does. It asserts the final states through the API, the four emails, that every event owed to the broker was published, and that no dead-letter queue holds a message. A run takes about a minute and a half, most of it the campaign's real 40-second deadline and the close job's 30-second pass.
- Ports, on 127.0.0.1 (`LOCAL_BIND`): the gateway on 8080 (`LOCAL_GATEWAY_PORT`), the Pledges service on 8081 for debugging only, Moto on 5001, PostgreSQL on 5432 and Valkey on 6379. `docker compose up -d` without the profile still starts only PostgreSQL and Valkey, for `dotnet run`.

## Layout

- `src/Services/<Name>/`: each service's outcome in one folder: its host (`<Name>.Host`), its module's layers (Domain, Application, Infrastructure), its Contracts, its `expectations.json` and its own tests. A `<Name>.Client` lives with its callers (`src/Services/Pledges/Payments.Client`). A host only composes its one module: the module brings its own services and endpoints (`AddCampaignsModule`, `MapCampaignsEndpoints`).
- `src/Platform/`: what every service shares. `BuildingBlocks` (its own solution, `BuildingBlocks.slnx`, versioned and published as NuGet packages; the services reference a pinned version): the result type, the dispatcher, the HTTP error mapping. `Messaging`: the broker adapter (SNS and SQS). `RemoteCalls`: the pipeline for calls between services.
- `src/Services/*/*.Contracts` (their own solution, `Contracts.slnx`): each module's events and remote commands, a versioned package per module like the BuildingBlocks, with its checked-in contract snapshot and recorded examples in `contract/`.
- `src/Gateway`: the public entry, routing each path to its service.
- `tests/`: architecture rules, unit tests per module, integration tests against a real database, and the contract tests: the producers' snapshots (`Contracts.ProducerTests`) and the consumers' reading of the published examples (`Contracts.ConsumerTests`).
- `infra/bootstrap/`: long-lived AWS resources (OpenTofu state bucket, ECR, CodeArtifact). Applied once and kept. Its own state is local; keep the `terraform.tfstate` file safe.
- `infra/env/`: the sandbox environment. Applied for a lesson or a test run, destroyed afterwards.
- `infra/modules/`: OpenTofu modules used by `infra/env/`.
- `scripts/replay.sh`: re-applies the lesson commits after an earlier lesson changes, and moves the tags.
- `scripts/split-diff.sh <from> <to>`: lists every change to a module's Domain or Application code between two refs, and fails on any that `split-allowed.yaml` does not cover. CI runs it from `s3-l16`, the last monolith tag, on every push to main and every pull request that touches `src/Services`. See [Checking the split](#checking-the-split).

## Checking the split

The course claims that a module can leave the monolith without changing its Domain or Application code. `scripts/split-diff.sh` checks that claim on the diff from `s3-l16`. Each changed file falls into one class:

- `permitted`: the split needed the change and no rule changed, such as a project reference.
- `boundary-correction`: a module boundary drawn wrong in the monolith, corrected inside it before the extraction. These are the only places where the split forced a domain change. The script counts them and prints each lesson and reason.
- `release-1-fix`: a Release 1 bug fix. The bug existed in one process already.
- `forbidden`: anything else. The check fails.

Release 1 fixes keep landing after `s3-l16`. There were two ways to keep them out of the count:

- Derive a new baseline from `s3-l16` plus every commit whose subject starts `fix:` and names an early CQA ticket.
- List each fix in `split-allowed.yaml`, one entry per ticket, with the files that ticket's commits changed.

The check uses the second way, because it is simpler and harder to abuse:

- **A derived baseline trusts a commit message.** Any commit titled `fix: CQA-123: …` would take its Domain changes out of the check, with nobody reviewing the list. A ticket-number cut-off says nothing about whether the split drove the change.
- **A derived baseline is fragile.** Building one means replaying those commits onto `s3-l16` apart from the rest, which breaks whenever a fix and a lesson touch the same lines.
- **An entry is one reviewed line** that names its class, its ticket and its reason.
- **An entry covers only its own work.** Every commit in the range that touched a listed file must name the entry's ticket or lesson in its subject. So an entry written for `CQA-210` does not cover a later Stage 4 commit to the same file.

`scripts/split-diff.test.sh` tests each class and each refusal on a throwaway repository.

## Requirements

.NET SDK 10, Docker, and [OpenTofu](https://opentofu.org) or [Terraform](https://developer.hashicorp.com/terraform), 1.13 or a later 1.x. An AWS account only for the lessons that deploy.

Both tools are supported. OpenTofu (`tofu`, MPL-2.0) is the default; Terraform (`terraform`, BUSL-1.1) works the same. The commands differ only in the program's name. The scripts run `tofu` unless `IAC` names the other: `IAC=terraform scripts/deploy-service.sh ...`. CI checks every root with both, at the versions in `.opentofu-version` and `.terraform-version`; `scripts/check-infra.sh` runs the same checks on your machine (`IAC=terraform scripts/check-infra.sh`).

One `.terraform.lock.hcl` per root serves both tools: the provider sources name the OpenTofu registry (`registry.opentofu.org/hashicorp/aws`), so Terraform and OpenTofu install the same provider builds and check the same hashes. Terraform reports them as "self-signed": the OpenTofu registry signs its copies of HashiCorp's MPL-2.0 providers with its own key.

The tags up to s4-l10 were written for Terraform; OpenTofu runs them too, with `tofu` in place of `terraform`.
