# Migration Cohorts

Audience: platform maintainers and migration agents.

## Purpose

Migration cohorts group repositories by CI shape.
Each cohort has a playbook and dedicated agent instructions.
Use them before editing consumer repositories.

## Cohorts

| Cohort | Typical repositories | Primary target |
|---|---|---|
| `legacy-dotnet-service` | ArkanisBackend, ArkanisDiscordBot, Hosting.Extensions.1Password | Replace old test/release/container jobs with platform workflows. |
| `modern-dotnet-package` | Template.NET, Aspire.Hosting.Extensions.Kubernetes, aspire-kubernetes-example-host | Normalize near-modern package workflows and remove release exec scripts. |
| `complex-app` | CitizenId, ArkanisOverlay | Preserve app-specific lanes while moving common setup, release, publish, and deploy work to reusable workflows. |
| `infra` | Infrastructure | Isolate plan/apply/cleanup trust zones and prepare for future OpenTofu platform workflows. |
| `node-docs` | CitizenId-docs, CitizenId-medusa-store | Move install/lint/test/build to split Node workflows and keep deploy/release/service orchestration separate. |

## Common Migration Flow

1. Read target repo instructions and workflow files.
2. Pick one cohort.
3. Produce a before/after workflow map.
4. Replace repeated setup with platform reusable workflows.
5. Split release, publish, and deploy side effects into separate jobs.
6. Encode runner selection through `runs-on` or `runs-on-json` plus `runs-on-self-hosted`.
7. Reduce workflow-level permissions to `permissions: {}` and job-level grants.
8. Run `actionlint`, platform validation, and a bounded `act` smoke test where practical.

## Agent Instructions

Each cohort directory contains an `AGENTS.md`.
Follow the closest cohort `AGENTS.md` while migrating repositories in that cohort.
Do not use these instructions to justify broad edits outside the selected cohort.
