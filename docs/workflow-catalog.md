# Workflow Catalog

Audience: consumers and platform maintainers.

## Public Workflows

| File | Purpose | Trust zone | Required caller permissions | Main artifacts |
|---|---|---|---|---|
| `wf-setup-dotnet.yml` | Restore, format, build, test, coverage, metadata, and diagnostics. | untrusted or trusted-build | `contents: read` | test results, coverage files, binlog, metadata, manifest |
| `wf-setup-dotnet-jetbrains.yml` | Verify JetBrains ReSharper CleanupCode produces no Git diff. | untrusted or trusted-build | `contents: read` | cleanup log, changed-file list, diff stat, diff preview, manifest |
| `wf-setup-node.yml` | Install, lint, test, build, metadata, and diagnostics. | untrusted or trusted-build | `contents: read` | install/lint/test/build logs, metadata, manifest |
| `wf-release-semantic.yml` | Run semantic-release metadata without `@semantic-release/exec`. | publish | `contents: write`, `issues: write`, `pull-requests: write` | release diagnostics |
| `wf-publish-nuget.yml` | Pack and publish one NuGet project. | publish | `contents: read`, `id-token: write` | `.nupkg`, `.snupkg`, manifest |
| `wf-publish-container.yml` | Publish one OCI image with optional .NET version stamping. | trusted-build or publish | `contents: read`, `packages: write`, `id-token: write`, `attestations: write` | digest, Buildx metadata, manifest |
| `wf-deploy-k8s-aspire.yml` | Deploy an Aspire AppHost to Kubernetes. | deploy | `contents: read`, `packages: read` | deploy output, manifest |
| `wf-platform-selftest.yml` | Validate platform workflow contracts. | trusted-build | `contents: read` | validation logs |

## Repository Workflows

| File | Purpose | Trust zone | Required permissions | Notes |
|---|---|---|---|---|
| `build.yml` | Run platform selftests for pull requests, main pushes, and manual dispatch. | untrusted or trusted-build | `contents: read` | Calls `wf-platform-selftest.yml` locally. |
| `release.yml` | Run selftest, then publish GitHub release metadata on main pushes. | publish | `contents: write`, `issues: write`, `pull-requests: write` | Calls `wf-release-semantic.yml` locally and uses `release.config.cjs`. |

## Common Inputs

| Input | Meaning |
|---|---|
| `runs-on-json` | JSON array passed to `runs-on`. |
| `runs-on-self-hosted` | True when `runs-on-json` targets self-hosted runners. |
| `enable-cache` | Enables dependency cache where the workflow uses `runs-on/cache`. |
| `timeout-minutes` | Job timeout. |
| `artifact-retention-days` | Diagnostic or output artifact retention. |

Use `runs-on-json` to override GitHub-hosted runner images or self-hosted runner labels.
Use `runs-on-self-hosted` to let workflows gate hosted-only and self-hosted-only assumptions.
Set `enable-cache` to false for cold-restore validation, cache incident isolation, or runners without cache service access.

## .NET Setup Workflow

`wf-setup-dotnet.yml` checks out the caller repository.
It sets up .NET, restores local tools, restores dependencies, optionally verifies formatting, builds with a binlog, optionally runs tests, optionally collects coverage, writes metadata, writes a manifest, writes a summary, and uploads diagnostics.

Preconditions:

- `solution` points to a solution or project in the caller repository.
- Lock files exist when `restore-locked-mode` is true.
- The selected runner can install or run the requested .NET SDK.

Side effects:

- Writes under `artifacts/`.
- Reads and writes NuGet dependency cache when `enable-cache` is true.
- Uploads diagnostics with `if: always()`.
- Runs `dotnet tool restore` when local tools exist.

## .NET JetBrains CleanupCode Workflow

`wf-setup-dotnet-jetbrains.yml` checks out the caller repository.
It sets up .NET 10 action tooling, sets up the requested project SDK, restores NuGet dependencies, restores or installs JetBrains ReSharper command line tools, runs `jb cleanupcode`, writes diff diagnostics, writes a manifest, writes a summary, and uploads diagnostics.
It is based on the CitizenId format job shape.
The default CleanupCode profile is `Built-in: Reformat & Apply Syntax Style`.
The default exclude filter is `**/*.razor;**/*.svg;**/*.md`.

Preconditions:

- `solution` points to a solution or project in the caller repository.
- Lock files exist when `restore-locked-mode` is true.
- The selected runner can install or run the requested .NET SDK.
- The selected runner can install .NET 10 SDK for the action file script.
- Local tool restore requires `JetBrains.ReSharper.GlobalTools` in `.config/dotnet-tools.json`.
- `install-tool` requires network access to NuGet and should set `tool-version` for repeatability.

Side effects:

- Writes under `artifacts/jetbrains-cleanupcode`.
- Reads and writes NuGet dependency cache when `enable-cache` is true.
- Runs CleanupCode, which may modify workspace files before the Git diff gate.
- Fails when CleanupCode creates a Git diff and `fail-on-diff` is true.

Example:

```yaml
jobs:
  cleanup:
    name: .NET JetBrains CleanupCode
    uses: ArkanisCorporation/ci/.github/workflows/wf-setup-dotnet-jetbrains.yml@v1
    permissions:
      contents: read
    with:
      runs-on-json: '["ubuntu-latest"]'
      runs-on-self-hosted: false
      dotnet-version: 10.0.x
      solution: CitizenId.slnx
      profile: "Built-in: Reformat & Apply Syntax Style"
      exclude: "**/*.razor;**/*.svg;**/*.md"
      enable-cache: true
```

## Node Setup Workflow

`wf-setup-node.yml` checks out the caller repository.
It sets up Node.js, prepares npm/pnpm/yarn, restores package-manager cache when enabled, installs dependencies with strict lockfile behavior, optionally runs lint/test/build scripts, writes metadata, writes a manifest, writes a summary, and uploads diagnostics.

Preconditions:

- `working-directory` contains package.json.
- A lockfile exists for the selected package manager unless `cache-dependency-path` points at matching lockfiles.
- pnpm and yarn projects either set `package-manager-version` or include package.json `packageManager`.
- Lifecycle scripts are blocked in the generated install command unless `allow-lifecycle-scripts` is true.

Side effects:

- Writes under `artifacts/`.
- Writes Corepack shims and package-manager downloads under `RUNNER_TEMP`.
- Reads and writes package-manager cache when `enable-cache` is true.
- Uploads diagnostics with `if: always()` when `upload-diagnostics` is true.

## Semantic Release Workflow

`wf-release-semantic.yml` runs semantic-release with Node 24 by default.
It rejects `@semantic-release/exec` unless `allow-exec-plugin` is explicitly true.
Verification and publishing must be modeled as separate workflow jobs.

Preconditions:

- The caller grants release permissions.
- The caller repository contains valid semantic-release configuration.
- Release branches are protected by caller policy.

Side effects:

- May create tags, releases, changelog commits, comments, or release notes depending on repository semantic-release config.
- Uploads release diagnostics.

## NuGet Publish Workflow

`wf-publish-nuget.yml` restores and packs one project.
It publishes packages with NuGet Trusted Publishing by default.
It can use `NUGET_API_KEY` when `trusted-publishing` is false.

Preconditions:

- The project is packable.
- `version` is the semantic version to pack.
- Trusted Publishing requires a nuget.org policy that matches the workflow requesting the OIDC token.
- API-key fallback requires the `NUGET_API_KEY` secret.

Side effects:

- Creates packages under `artifacts/nuget`.
- Reads and writes NuGet dependency cache when `enable-cache` is true.
- Publishes packages unless `dry-run` is true.

## Container Publish Workflow

`wf-publish-container.yml` uses Docker Buildx.
It supports GitHub-hosted Docker, self-hosted Docker, and remote BuildKit endpoints.
It can run the `dotnet-setversion` composite action before Docker Buildx for .NET container images.
It appends a non-secret `VERSION=<version>` Docker build argument when `version` is set and `build-args` does not already define `VERSION`.
`version` is always the bare semantic version, such as `1.2.3`.
`version-tag` is only for image tags and may use the release tag form, such as `v1.2.3`.

Preconditions:

- The runner can run Docker Buildx or reach the configured remote BuildKit endpoint.
- Registry credentials are available when `push` is true.
- `version` is a bare SemVer value without a leading `v` when set.
- `dotnet-setversion` requires .NET project files under `dotnet-setversion-working-directory`.
- `dotnet-setversion` requires Bash, network access to restore actions/tool packages, and `github.workflow_ref` / `github.workflow_sha` support from reusable workflows.

Side effects:

- Builds container layers before pushing them when `push` is true.
- Pushes registry tags when `push` is true.
- Checks out this CI platform repository under `.ci/arkanis-ci` when `dotnet-setversion` is true, then removes that checkout before Docker Buildx runs.
- Modifies matched `.csproj` files before Docker Buildx when `dotnet-setversion` is true.
- Passes Docker build args to BuildKit; never put secrets in `build-args`.
- Emits a digest output for downstream deploys.

Example:

```yaml
jobs:
  publish_web:
    name: Publish web image
    uses: ArkanisCorporation/ci/.github/workflows/wf-publish-container.yml@v1
    permissions:
      contents: read
      packages: write
      id-token: write
      attestations: write
    with:
      runs-on-json: '["ubuntu-latest"]'
      runs-on-self-hosted: false
      image: ghcr.io/arkaniscorporation/example-web
      context: .
      dockerfile: src/Web/Dockerfile
      version: ${{ needs.release.outputs.new-version }}
      version-tag: ${{ needs.release.outputs.new-tag }}
      version-channel: ${{ needs.release.outputs.new-channel }}
      dotnet-setversion: true
      push: true
    secrets:
      REGISTRY_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

## Aspire Kubernetes Deploy Workflow

`wf-deploy-k8s-aspire.yml` deploys with `dotnet tool run aspire -- deploy`.
It accepts an optional `KUBE_CONFIG` secret.
If `KUBE_CONFIG` is omitted, the runner must already have a valid kube context.

Preconditions:

- The runner can reach the Kubernetes API.
- The AppHost project exists.
- The target namespace is a valid Kubernetes namespace.

Side effects:

- Creates the namespace when missing.
- Reads and writes NuGet dependency cache when `enable-cache` is true.
- Applies deployment changes unless `dry-run` is true.
- Writes deploy output under `output-path/environment-name`.
