# Reusable workflow directory

Place public workflow API files here. GitHub requires reusable workflow files directly under `.github/workflows`.

Do not add subdirectories for workflows. Use filename prefixes instead.

Start each workflow from `AGENTS.md` contract, then add schema in `schemas/workflow-inputs/`, docs in `docs/workflow-catalog.md`, fixture in `tests/fixtures/`.

## Repository workflows

`verify-release.yml` is this repository's pull request and manual release verification pipeline.
It runs `wf-platform-selftest.yml`, the TypeScript pnpm fixture, the .NET NuGet fixture, and the .NET container fixture before read-only release verification.
`release.yml` is this repository's default release pipeline for pull requests, main pushes, and manual dispatches.
On pull requests, it runs the same fixture dogfood series before calling `wf-verify-release-semantic.yml`.
On main pushes and manual dispatches, it runs the same fixture dogfood series before calling `wf-release-semantic.yml` for trusted publication paths.
It publishes GitHub release metadata and updates mutable major version tags such as `v1`.
`verify-release.yml` installs the same semantic-release major-tag plugin as production so dry-runs load the same configuration.

## Verification Workflows

`wf-verify-release-semantic.yml` runs semantic-release in dry-run mode with read-only permissions.
`wf-verify-publish-nuget.yml` packs NuGet packages without credentials or environments.
`wf-verify-publish-container-dotnet.yml` builds .NET container images without pushing.
`wf-verify-deploy-k8s-aspire.yml` validates deployment inputs without configuring kube credentials or applying changes.

## .NET Container Publish

`wf-publish-container-dotnet.yml` is the reusable .NET container publishing workflow.
It stamps .NET project versions before Docker Buildx runs.
It binds publication to `environment-name` and always pushes.
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
