# legacy-dotnet-service/AGENTS.md

## Scope

Use these instructions for older .NET service repositories.
Known examples are ArkanisBackend, ArkanisDiscordBot, and Hosting.Extensions.1Password.

## Goal

Move duplicated test, Docker, release, and deployment setup into reusable platform workflows.
Remove script-driven publishing from semantic-release.

## Inspection Order

1. Read `_test.yaml`, `_release.yaml`, `build.yaml`, and `_deploy-*` workflows.
2. Identify .NET SDK version, solution path, Docker image name, release branch, deploy endpoint, and backpropagation behavior.
3. Record existing secrets and environments before changing permissions.

## Target Shape

- Use `wf-dotnet-format.yml` and `wf-dotnet-test.yml` for format, build, tests, coverage, and diagnostics.
- Set `run-dotnet-format: false` on `wf-dotnet-format.yml` only when the legacy service has CleanupCode without ordinary `dotnet format`.
- Use `wf-release-semantic.yml` only for release metadata.
- Use `wf-publish-container-dotnet.yml` for .NET container publish.
- Pass bare release versions through `version` and release tags through `version-tag`.
- Do not set old `dotnet-setversion` flags.
- `wf-publish-container-dotnet.yml` stamps assemblies before Docker Buildx by default.
- Keep deployment in a separate deploy workflow or job.
- Keep release backpropagation separate from release metadata.

## Rules

- Do not keep `@semantic-release/exec` for verification, Docker publish, package publish, or deployment.
- Do not preserve workflow-level write permissions when only release or deploy needs them.
- Do not hardcode `ubuntu-latest` when the repository has runner variables or self-hosted deploys.
- Do not merge deploy jobs into the release job.
- Do not assume Docker socket access on self-hosted runners without labels and preflight.

## Verification

- Run `actionlint` for changed workflows.
- Run platform validation in this repository when examples or schemas change.
- Run target repo build tests after migrating consumer workflows.
- Confirm Docker tags and release outputs match previous behavior before enabling publish.
