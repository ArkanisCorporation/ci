# Reusable workflow directory

Place public workflow API files here. GitHub requires reusable workflow files directly under `.github/workflows`.

Do not add subdirectories for workflows. Use filename prefixes instead.

Start each workflow from `AGENTS.md` contract, then add schema in `schemas/workflow-inputs/`, docs in `docs/workflow-catalog.md`, fixture in `tests/fixtures/`.

## Repository workflows

`release.yml` is this repository's default release pipeline for pull requests, main pushes, and manual dispatches.
On pull requests, it runs `wf-platform-selftest.yml`, split TypeScript pnpm lint/test/build fixtures, split .NET format/test fixtures, the .NET NuGet fixture, and the .NET container fixture before calling `wf-verify-release-semantic.yml`.
On main pushes and manual dispatches, it runs the same fixture dogfood series before calling `wf-release-semantic.yml` for trusted publication paths.
Each repository pipeline job passes `runs-on: ${{ vars.RUNNER_DEFAULT || 'daedalus' }}` and `runs-on-self-hosted: true` into the reusable workflows.
It publishes GitHub release metadata and updates mutable major version tags such as `v1`.

## Verification Workflows

`wf-verify-release-semantic.yml` runs semantic-release in dry-run mode with `contents: write` so semantic-release can verify tag push authorization.
`wf-verify-publish-nuget.yml` packs NuGet packages without credentials or environments through `.github/actions/dotnet-pack-nuget`.
`wf-verify-publish-container-dotnet.yml` builds .NET container images without pushing.
`wf-verify-deploy-k8s-aspire.yml` validates deployment inputs without configuring kube credentials or applying changes.

## .NET NuGet Publish

Use `.github/actions/dotnet-pack-nuget` and `.github/actions/dotnet-publish-nuget` when NuGet Trusted Publishing must match the consumer repository workflow file.
The consumer workflow should run `NuGet/login` between those two actions in the same protected job.
`wf-publish-nuget.yml` remains available for environment-gated workflow reuse and API-key fallback, but caller-owned Trusted Publishing is the safer default for nuget.org policies.

## Private NuGet Restore

.NET reusable workflows accept optional `NUGET_AUTH_JSON` and `OP_SERVICE_ACCOUNT_TOKEN` secrets.
`NUGET_AUTH_JSON` may contain multiple package source credentials, including literal values, `op://` 1Password references, `github://actor`, and `github://token`.
Container workflows only pass private restore credentials to Docker BuildKit when `nuget-build-secret` is true.
Dockerfiles that need the generated config should mount the `nuget_config` secret during restore.

## .NET Container Publish

`wf-publish-container-dotnet.yml` is the reusable .NET container publishing workflow.
It stamps .NET project versions before Docker Buildx runs.
It defaults to a generated GitHub Actions BuildKit cache when `enable-cache` is true and no explicit Buildx cache inputs are set.
It binds publication to `environment-name` and always pushes.
Use `extra-tags` for additional mutable tags such as `latest`.

## .NET Generated Code

`wf-setup-dotnet-generated-code.yml` is the reusable generated-source diff gate.
It supports CitizenId-style Wolverine generated handler checks by building once, running host codegen commands, and failing when generated paths change.

## .NET Setup, Format, And Test

`.github/actions/setup-dotnet` is the internal setup composite used by .NET verification workflows.
`wf-dotnet-format.yml` optionally runs `dotnet format --verify-no-changes` and always runs JetBrains CleanupCode verification.
`wf-dotnet-test.yml` builds, tests, and handles coverage output.

## Node Setup, Lint, Test, And Build

`.github/actions/setup-node` is the internal setup composite used by Node verification workflows.
`wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` run the expensive script lanes independently.

## GitHub Actions Lint

`wf-lint-github-actions.yml` is the reusable caller workflow lint gate.
It sets up Node.js because raven-actions/actionlint may call npm on cold cache misses.
It runs actionlint with read-only repository permissions.

## Release Backpropagation

`wf-release-backpropagation.yml` creates a release branch pull request back to the default branch.
It can approve with `PR_AUTOMATION_PAT` and enable auto-merge with GitHub CLI.
It binds the job to `environment-name` so the environment can provide `PR_AUTOMATION_PAT`.
