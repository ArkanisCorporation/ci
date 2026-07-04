# ADR-0004: Split setup and verification workflows

Status: accepted

## Context

`wf-setup-dotnet.yml` and `wf-setup-node.yml` used setup-oriented names while running expensive verification work.
The .NET workflow restored, formatted, built, tested, collected coverage, and produced coverage reports.
The Node workflow installed dependencies, linted, tested, and built.
Consumers need format, lint, test, and build lanes to run independently and in parallel.
GitHub reusable workflows still resolve relative local actions from the checked-out workspace, so reusable workflows that use this platform repository's composite actions must check out the called workflow repository first.
Last verified: 2026-07-04.

## Decision

Keep `wf-setup-dotnet.yml` as .NET setup and restore only.
Add `wf-dotnet-format.yml` for `dotnet format --verify-no-changes`.
Add `wf-dotnet-test.yml` for build, test, coverage collection, and coverage report publishing.
Keep `wf-setup-node.yml` as Node package-manager setup and install only.
Add `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` for separate Node script lanes.
Add `.github/actions/setup-node` so Node setup logic is shared by all Node workflows.
Keep `.github/actions/setup-dotnet` as the shared .NET setup action.

## Consequences

This is a breaking workflow contract change for callers that expected aggregate verification from `wf-setup-dotnet.yml` or `wf-setup-node.yml`.
Callers must opt into the separate verification workflows they need.
Parallel jobs may repeat dependency setup, but caches keep the repeated work bounded.
Each expensive lane now has its own timeout, permissions, diagnostics artifact, and summary.

## Migration

Replace a single .NET setup job with separate `wf-setup-dotnet.yml`, `wf-dotnet-format.yml`, and `wf-dotnet-test.yml` jobs when all lanes are required.
Replace a single Node setup job with separate `wf-setup-node.yml`, `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` jobs when all lanes are required.
Grant `pull-requests: write` only to `wf-dotnet-test.yml` callers that enable `coverage-pr-comment`.
Keep `contents: read` for setup, format, lint, test without PR comments, and build lanes.

## References

- GitHub Actions reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub Actions workflow syntax: https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax
- GitHub Community discussion on composite actions inside reusable workflows: https://github.com/orgs/community/discussions/18601
