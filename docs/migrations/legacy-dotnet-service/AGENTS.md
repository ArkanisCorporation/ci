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

- Use `wf-setup-dotnet.yml` for restore, format, build, tests, coverage, and diagnostics.
- Use `wf-setup-dotnet-jetbrains.yml` when the legacy service already has JetBrains CleanupCode as a CI or pre-commit gate.
- Use `wf-release-semantic.yml` only for release metadata.
- Use `wf-publish-container.yml` for container publish.
- Pass bare release versions through `version` and release tags through `version-tag`.
- Set `dotnet-setversion: true` for .NET images so assemblies are stamped before Docker Buildx.
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
