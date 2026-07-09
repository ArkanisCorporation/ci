# Arkanis CI

Shared GitHub Actions platform for Arkanis repositories.
Reusable workflows are the public API.
Composite actions are optional step bundles for same-repository workflows or explicitly checked-out platform code.

## Quick Navigation

| Area | Use when |
|---|---|
| [Platform docs](docs/README.md) | You need workflow contracts, runner rules, artifacts, caching, or security notes. |
| [Migration guides](docs/migrations/README.md) | You are moving an existing repository onto the shared workflow platform. |
| [Mock project fixtures](tests/fixtures/mock-projects/README.md) | You need runnable TypeScript or .NET test data for local workflow smoke tests. |
| [.NET library example](examples/dotnet-library/README.md) | You need a package-style .NET consumer workflow. |
| [.NET container example](examples/dotnet-webapi-container/README.md) | You need a .NET web API container publish workflow. |
| [Node pnpm example](examples/node-pnpm/README.md) | You need a Node.js pnpm verification workflow. |
| [Mixed monorepo example](examples/mixed-monorepo/README.md) | You need coordinated .NET and Node verification. |
| [Kubernetes deploy example](examples/k8s-helm-deploy/README.md) | You need a Kubernetes or Helm-oriented deployment shape. |
| [Python Poetry example](examples/python-poetry/README.md) | You need the current Python example scope and limitations. |

## Public Workflows

| Workflow | Purpose |
|---|---|
| `wf-dotnet-format.yml` | Verify optional `dotnet format` and mandatory JetBrains CleanupCode without running tests. |
| `wf-dotnet-test.yml` | Build, test, collect coverage, metadata, and diagnostics for .NET repositories. |
| `wf-setup-dotnet-generated-code.yml` | Verify generated .NET source stays committed after codegen commands. |
| `wf-node-lint.yml` | Run one Node lint script or command. |
| `wf-node-test.yml` | Run one Node test script or command. |
| `wf-node-build.yml` | Run one Node build script or command. |
| `wf-lint-github-actions.yml` | Lint caller GitHub Actions workflows with actionlint. |
| `wf-verify-release-semantic.yml` | Verify semantic-release in dry-run mode, including tag push authorization. |
| `wf-release-semantic.yml` | Publish semantic-release metadata without `@semantic-release/exec` verification or publishing scripts. |
| `wf-release-backpropagation.yml` | Create and optionally auto-merge release branch backpropagation PRs. |
| `wf-verify-publish-nuget.yml` | Pack NuGet packages without publishing or requesting NuGet credentials. |
| `wf-publish-nuget.yml` | Pack and publish NuGet packages through Trusted Publishing or API-key fallback. |
| `wf-verify-publish-container-dotnet.yml` | Stamp .NET versions and build OCI images without pushing. |
| `wf-publish-container-dotnet.yml` | Stamp .NET versions and publish OCI images through Docker Buildx. |
| `wf-verify-deploy-k8s-aspire.yml` | Verify Aspire Kubernetes deployment inputs without applying cluster changes. |
| `wf-deploy-k8s-aspire.yml` | Deploy an Aspire AppHost to Kubernetes. |
| `wf-platform-selftest.yml` | Validate this platform repository. |

## Runner Model

Every public workflow accepts `runs-on`.
Use it for a single runner label such as `ubuntu-latest`.

Every public workflow accepts `runs-on-json`.
Use it when a caller needs a full JSON label array such as `["self-hosted","linux","x64","arc","dotnet"]`.
When `runs-on-json` is set, it overrides `runs-on`.

Every public workflow accepts `runs-on-self-hosted`.
Set it to `true` when the effective runner selection targets self-hosted runners.
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
  dotnet-format:
    name: .NET format
    uses: ArkanisCorporation/ci/.github/workflows/wf-dotnet-format.yml@v1
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      enable-cache: true
      global-json-file: global.json
      solution: CitizenId.slnx

  dotnet-test:
    name: .NET test
    uses: ArkanisCorporation/ci/.github/workflows/wf-dotnet-test.yml@v1
    permissions:
      contents: read
      pull-requests: write
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      enable-cache: true
      global-json-file: global.json
      solution: CitizenId.slnx
      coverage-pr-comment: true
```

## Private NuGet Restore

.NET workflows accept an optional `NUGET_AUTH_JSON` secret for private package source credentials.
The caller repository should commit non-secret package sources in `NuGet.Config`.
The `name` values in `NUGET_AUTH_JSON` must match the package source keys in `NuGet.Config`.
Multiple credentials are provided by adding more entries to the `sources` array.

Literal single-feed shape:

```json
{
  "version": 1,
  "sources": [
    {
      "name": "internal",
      "source": "https://nuget.example.com/v3/index.json",
      "username": "ci",
      "password": "PRIVATE_TOKEN_STORED_IN_THE_SECRET",
      "validAuthenticationTypes": "Basic",
      "protocolVersion": "3"
    }
  ]
}
```

Mixed GitHub Packages and 1Password shape:

```json
{
  "version": 1,
  "sources": [
    {
      "name": "github",
      "source": "https://nuget.pkg.github.com/ArkanisCorporation/index.json",
      "username": "github://actor",
      "password": "github://token",
      "validAuthenticationTypes": "Basic",
      "protocolVersion": "3"
    },
    {
      "name": "internal",
      "source": "https://nuget.example.com/v3/index.json",
      "username": "op://ci-nuget/internal-feed/username",
      "password": "op://ci-nuget/internal-feed/token",
      "validAuthenticationTypes": "Basic",
      "protocolVersion": "3"
    }
  ]
}
```

Reusable workflow call shape:

```yaml
jobs:
  dotnet-test:
    uses: ArkanisCorporation/ci/.github/workflows/wf-dotnet-test.yml@v1
    permissions:
      contents: read
      pull-requests: write
    with:
      solution: CitizenId.slnx
      dotnet-version: 10.0.x
    secrets:
      NUGET_AUTH_JSON: ${{ secrets.ARKANIS_NUGET_AUTH_JSON }}
      OP_SERVICE_ACCOUNT_TOKEN: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}
```

Container workflows require `nuget-build-secret: true` when Dockerfile restore needs private feeds.
The Dockerfile must mount the generated BuildKit secret instead of copying a credentialed config into the image.

```dockerfile
# syntax=docker/dockerfile:1
RUN --mount=type=secret,id=nuget_config,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet restore src/App/App.csproj --locked-mode
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
  node-lint:
    name: Node lint
    uses: ArkanisCorporation/ci/.github/workflows/wf-node-lint.yml@v1
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: .
      enable-cache: true

  node-test:
    name: Node test
    uses: ArkanisCorporation/ci/.github/workflows/wf-node-test.yml@v1
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: .
      enable-cache: true

  node-build:
    name: Node build
    uses: ArkanisCorporation/ci/.github/workflows/wf-node-build.yml@v1
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: .
      enable-cache: true
```

## Release Shape

Release verification and release publication use separate workflows.
`wf-verify-release-semantic.yml` exercises semantic-release without environments, but it requires `contents: write` because semantic-release verifies tag push access even in dry-run mode.
`wf-release-semantic.yml` publishes release metadata from an environment-gated job.
Package publishing, image publishing, and deployment consume release outputs in separate jobs.
Use `wf-verify-publish-*` and `wf-verify-deploy-*` workflows for dry-run style validation without environments.
Container publishing passes the bare semantic-release version as `version` and the tagged release ref as `version-tag`.
For .NET images, use `wf-publish-container-dotnet.yml` so assemblies are stamped before Docker Buildx runs.
Use `extra-tags` for additional mutable tags such as `latest`.
Jobs that call reusable workflows can use `strategy.matrix` for independent publish targets.
Use that shape for multiple container images, NuGet packages, verification targets, or deploy targets that share the same release outputs.
Pass matrix values through `with`, and keep each matrix entry explicit enough to name the project, image, Dockerfile, or environment it owns.
Do not aggregate matrix reusable-workflow outputs directly; use the emitted artifacts or manifests when a downstream job needs all results.

```yaml
jobs:
  publish_images:
    name: Image - ${{ matrix.target }}
    if: ${{ needs.release.outputs.new-version != '' }}
    needs: release
    strategy:
      fail-fast: false
      matrix:
        include:
          - target: web
            image: ghcr.io/arkaniscorporation/example-web
            dockerfile: src/Web/Dockerfile
            version-working-directory: src/Web
          - target: worker
            image: ghcr.io/arkaniscorporation/example-worker
            dockerfile: src/Worker/Dockerfile
            version-working-directory: src/Worker
    uses: ArkanisCorporation/ci/.github/workflows/wf-publish-container-dotnet.yml@v1
    permissions:
      contents: read
      packages: write
      id-token: write
      attestations: write
    with:
      runs-on: ${{ vars.RUNNER_DEFAULT || 'ubuntu-latest' }}
      runs-on-self-hosted: false
      image: ${{ matrix.image }}
      context: .
      dockerfile: ${{ matrix.dockerfile }}
      version: ${{ needs.release.outputs.new-version }}
      version-tag: ${{ needs.release.outputs.new-tag }}
      version-channel: ${{ needs.release.outputs.new-channel }}
      version-working-directory: ${{ matrix.version-working-directory }}
      environment-name: publish-ghcr
    secrets:
      REGISTRY_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

## Repository Pipeline

This repository dogfoods its platform workflows.
[release.yml](.github/workflows/release.yml) runs the same fixture dogfood series on pull requests, main pushes, and manual dispatches.
The repository pipeline jobs use `vars.RUNNER_DEFAULT` for runner selection and fall back to `daedalus`.
Pull requests call `wf-verify-release-semantic.yml` after the fixture jobs pass.
Main pushes and manual dispatches call `wf-release-semantic.yml` after the fixture jobs pass.
[release.config.cjs](release.config.cjs) publishes GitHub release metadata and updates mutable major version tags such as `v1`.
It intentionally excludes `@semantic-release/exec` and `@semantic-release/npm`.
The repository release workflow installs `semantic-release-major-tag@0.3.2` so dry-runs load the same semantic-release configuration as production.

## Local Validation

```bash
dotnet run --file scripts/generate-docs.cs -- --check
dotnet run --file scripts/validate-workflows.cs
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:1.7.12 -color
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/act-local.ps1 workflow_dispatch -W .github/workflows/wf-platform-selftest.yml -j validate
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/act-local.ps1 workflow_dispatch -W tests/fixtures/workflow-contract/typescript-pnpm-local.yml -j node-test
```

The validation script requires version tags for external actions.
It also checks that generated workflow input tables in `docs/workflow-catalog.md` are current.
The platform selftest pins actionlint 1.7.12 through `raven-actions/actionlint@v2` before running the validator.
The repository `.actrc` pins the local runner image, architecture, artifact server path, and pull behavior.
The `scripts/act-local.ps1` launcher preserves an existing `DOCKER_HOST`, and on Windows defaults to Docker Desktop's Linux engine pipe.
The platform selftest `act` command runs the platform self-test locally.
The TypeScript fixture `act` command runs one bounded mock-project smoke test.
