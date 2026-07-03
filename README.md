# Arkanis CI

Shared GitHub Actions platform for Arkanis repositories.
Reusable workflows are the public API.
Composite actions are optional step bundles for same-repository workflows or explicitly checked-out platform code.

## Public Workflows

| Workflow | Purpose |
|---|---|
| `wf-setup-dotnet.yml` | Restore, format, build, test, coverage, metadata, and diagnostics for .NET repositories. |
| `wf-setup-dotnet-jetbrains.yml` | Verify JetBrains ReSharper CleanupCode produces no Git diff. |
| `wf-setup-node.yml` | Install, lint, test, build, metadata, and diagnostics for Node.js repositories. |
| `wf-release-semantic.yml` | Run semantic-release without `@semantic-release/exec` verification or publishing scripts. |
| `wf-publish-nuget.yml` | Pack and publish NuGet packages through Trusted Publishing or API-key fallback. |
| `wf-publish-container.yml` | Publish OCI images through Docker Buildx with optional .NET version stamping. |
| `wf-deploy-k8s-aspire.yml` | Deploy an Aspire AppHost to Kubernetes. |
| `wf-platform-selftest.yml` | Validate this platform repository. |

## Runner Model

Every public workflow accepts `runs-on-json`.
Use it to select GitHub-hosted images such as `["ubuntu-latest"]` or self-hosted labels such as `["self-hosted","linux","x64","arc","dotnet"]`.

Every public workflow accepts `runs-on-self-hosted`.
Set it to `true` when `runs-on-json` targets self-hosted runners.
The workflows use it to gate preflight and runner-specific behavior.

## Consumer Example

```yaml
name: build

on:
  pull_request:
  push:
    branches: [main]

permissions: {}

jobs:
  dotnet:
    name: .NET setup
    uses: ArkanisCorporation/ci/.github/workflows/wf-setup-dotnet.yml@v1
    permissions:
      contents: read
    with:
      runs-on-json: '["ubuntu-latest"]'
      runs-on-self-hosted: false
      enable-cache: true
      global-json-file: global.json
      solution: CitizenId.slnx
```

Node projects use the same runner model.

```yaml
name: build

on:
  pull_request:
  push:
    branches: [main]

permissions: {}

jobs:
  node:
    name: Node setup
    uses: ArkanisCorporation/ci/.github/workflows/wf-setup-node.yml@v1
    permissions:
      contents: read
    with:
      runs-on-json: '["ubuntu-latest"]'
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: .
      enable-cache: true
```

## Release Shape

Verification runs before release as separate workflow jobs.
`wf-release-semantic.yml` only decides and publishes release metadata.
Package publishing, image publishing, and deployment consume release outputs in separate jobs.
Container publishing passes the bare semantic-release version as `version` and the tagged release ref as `version-tag`.
For .NET images, set `dotnet-setversion: true` so assemblies are stamped before Docker Buildx runs.

## Repository Pipeline

This repository dogfoods its platform workflows.
[build.yml](.github/workflows/build.yml) runs `wf-platform-selftest.yml` on pull requests, pushes to `main`, and manual dispatch.
[release.yml](.github/workflows/release.yml) runs the same selftest before calling `wf-release-semantic.yml` on pushes to `main`.
[release.config.cjs](release.config.cjs) publishes GitHub release metadata only.
It intentionally excludes `@semantic-release/exec` and `@semantic-release/npm`.

## Local Validation

```bash
dotnet run --file scripts/validate-workflows.cs
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:1.7.12 -color
act workflow_dispatch -W .github/workflows/wf-platform-selftest.yml -j validate -P ubuntu-latest=ghcr.io/catthehacker/ubuntu:act-latest
```

The validation script requires version tags for external actions.
The `act` command runs the platform self-test locally.
