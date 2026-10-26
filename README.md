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

## Layout

- `src/`: the API host, the modules (Campaigns, Pledges, Payments, Notifications) and the BuildingBlocks.
- `tests/`: architecture rules, unit tests per module, integration tests against a real database.
- `infra/bootstrap/`: long-lived AWS resources (Terraform state bucket, ECR, CodeArtifact). Applied once and kept. Its own state is local; keep the `terraform.tfstate` file safe.
- `infra/env/`: the sandbox environment. Applied for a lesson or a test run, destroyed afterwards.
- `infra/modules/`: Terraform modules used by `infra/env/`.
- `scripts/replay.sh`: re-applies the lesson commits after an earlier lesson changes, and moves the tags.

## Requirements

.NET SDK 10, Docker, Terraform 1.10 or later. An AWS account only for the lessons that deploy.
