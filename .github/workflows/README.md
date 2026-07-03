# Reusable workflow directory

Place public workflow API files here. GitHub requires reusable workflow files directly under `.github/workflows`.

Do not add subdirectories for workflows. Use filename prefixes instead.

Start each workflow from `AGENTS.md` contract, then add schema in `schemas/workflow-inputs/`, docs in `docs/workflow-catalog.md`, fixture in `tests/fixtures/`.

## Repository workflows

`release.yml` is this repository's pull request, main push, manual self-test, and release pipeline.
It runs `wf-platform-selftest.yml` before `wf-release-semantic.yml`.
It publishes GitHub release metadata only.

## .NET Container Publish

`wf-publish-container-dotnet.yml` is the reusable .NET container publishing workflow.
It stamps .NET project versions before Docker Buildx runs.
Use `extra-tags` for additional mutable tags such as `latest`.

## .NET JetBrains CleanupCode

`wf-setup-dotnet-jetbrains.yml` is the reusable CleanupCode verification workflow.
It keeps CitizenId-style JetBrains cleanup checks separate from ordinary `dotnet format`, build, and test verification.
It installs .NET 10 action tooling so the composite action can run its `CliWrap` file script.

## .NET Generated Code

`wf-setup-dotnet-generated-code.yml` is the reusable generated-source diff gate.
It supports CitizenId-style Wolverine generated handler checks by building once, running host codegen commands, and failing when generated paths change.

## GitHub Actions Lint

`wf-lint-github-actions.yml` is the reusable caller workflow lint gate.
It runs actionlint with read-only repository permissions.

## Release Backpropagation

`wf-release-backpropagation.yml` creates a release branch pull request back to the default branch.
It can approve with `PR_AUTOMATION_PAT` and enable auto-merge with GitHub CLI.
