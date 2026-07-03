## Target architecture

Central repo = **CI/CD platform product**, not YAML dump. Public API = **reusable workflows** in `.github/workflows/*.yml`; impl API = **composite actions** in `.github/actions/**`. Reason: reusable workflows can own jobs, runners, permissions, matrices, secrets, outputs; composites bundle steps inside one job. Reusable workflow files must live directly under `.github/workflows`, no subdirs, with `on: workflow_call`. Called workflow executes in caller repo context; `actions/checkout` checks out caller repo, not platform repo. ([GitHub Docs][1])

## 2026 deltas to adopt now

* **Node24-compatible action catalog**. Node20 reached EOL Apr 2026; GitHub runners switched default JS runtime to Node24 from Jun 16 2026, with insecure Node20 opt-out only transitional. Self-host fleets must track this. ([The GitHub Blog][2])
* **Self-host runner freshness policy**. New runner registration requires `>=2.329.0`; runners must stay updated within 30 days, with enforcement waves in 2026. Build runner images as cattle, not pets. ([The GitHub Blog][3])
* **Native parallel/background steps**. Use for same-runner independent work, service warmups, telemetry. Limits: not inside composite actions, max 10 parallel/background steps. ([The GitHub Blog][4])
* **Read-only cache for untrusted triggers**. GitHub now gives read-only cache tokens for default-branch events triggered without write perms. Use push/schedule cache warmers for write path. ([The GitHub Blog][5])
* **Non-zipped artifacts**. `upload-artifact` v7 + `archive:false`; `download-artifact` v8 required. Useful for HTML reports, Markdown, images, logs. ([The GitHub Blog][6])
* **Deployment concurrency queues**. `queue: max` with `cancel-in-progress: false`/unset allows up to 100 queued runs per concurrency group -> ordered env deploys. ([The GitHub Blog][7])
* **Immutable releases + attestations**. New GitHub immutable releases lock assets/tags after publish; artifact attestations establish provenance for build outputs. ([The GitHub Blog][8])
* **NuGet Trusted Publishing**. Prefer OIDC -> short-lived NuGet credential over long-lived API key; fallback secret only for legacy feeds. ([Microsoft for Developers][9])

## Repository structure

```text
gha-platform/
  README.md
  SECURITY.md
  CONTRIBUTING.md
  CHANGELOG.md

  .github/
    workflows/
      wf-setup-dotnet.yml
      wf-setup-node.yml
      wf-setup-python.yml
      wf-setup-monorepo.yml
      wf-pack-dotnet-nuget.yml
      wf-publish-nuget.yml
      wf-publish-container.yml
      wf-release.yml
      wf-deploy-k8s.yml
      wf-security-scan.yml
      wf-required-check.yml
      wf-platform-selftest.yml

    actions/
      setup-dotnet/
        action.yml
      setup-node/
        action.yml
      setup-python/
        action.yml
      detect-changes/
        action.yml
      compute-version/
        action.yml
      artifact-manifest/
        action.yml
      upload-ci-artifacts/
        action.yml
      publish-summary/
        action.yml
      docker-metadata/
        action.yml
      kube-auth/
        action.yml
      collect-diagnostics/
        action.yml

  docs/
    quickstart.md
    workflow-catalog.md
    input-contracts.md
    security-model.md
    permissions.md
    artifacts.md
    observability.md
    caching.md
    runner-contract.md
    arc-kubernetes.md
    releases.md
    nuget.md
    containers.md
    deployments.md
    monorepos.md
    troubleshooting.md
    edge-cases.md
    adr/
      0001-reusable-workflows-vs-composite-actions.md
      0002-sha-pinning-policy.md
      0003-artifact-contract.md
      0004-runner-contract.md
      0005-release-strategy.md

  examples/
    dotnet-library/
    dotnet-webapi-container/
    node-pnpm/
    python-poetry/
    mixed-monorepo/
    k8s-helm-deploy/

  policies/
    allowed-actions.yml
    permissions-baseline.yml
    artifact-retention.yml
    cache-policy.yml
    runner-labels.yml
    release-channels.yml

  schemas/
    workflow-inputs/
      wf-setup-dotnet.schema.json
      wf-publish-container.schema.json
      wf-deploy-k8s-aspire.schema.json
    artifact-manifest.schema.json
    run-metadata.schema.json

    scripts/
      validate-workflows.cs
    generate-docs.ps1
    generate-action-catalog.ps1

  tests/
    fixtures/
    workflow-contract/
    action-contract/
    snapshots/
```

Naming: `wf-*` for public reusable workflows; `.github/actions/*` for private shared step bundles. Keep workflow names stable; inputs stable; outputs machine-readable.

## Workflow catalog

| Workflow                   | Purpose                            | Key inputs                                                                                              | Outputs/artifacts                                                 | Notes                                                                                                                    |
| -------------------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `wf-setup-dotnet.yml`         | restore/build/test/coverage        | `dotnet-version`, `global-json`, `solution`, `configuration`, `test-filter`, `coverage`, `enable-cache`, `runs-on-json` | TRX/JUnit, coverage XML/HTML, MSBuild binlog, `run-metadata.json` | `restore --locked-mode`; `build --no-restore`; `test --no-build`; optional package cache via `runs-on/cache`. |
| `wf-setup-node.yml`           | TS/JS lint/test/build              | `node-version`, `package-manager`, `package-manager-version`, `working-directory`, `script-*`, `enable-cache`, `runs-on-json` | install/lint/test/build logs, `run-metadata.json`                 | `setup-node` + Corepack; strict lockfile installs; optional package-manager store cache via `runs-on/cache`. ([GitHub][11]) |
| `wf-setup-python.yml`         | Python lint/test/typecheck         | `python-version`, `package-manager`, `workspace`, `test-command`                                        | JUnit, coverage, env report                                       | use `setup-python` cache for pip/pipenv/poetry where applicable. ([GitHub][12])                                          |
| `wf-setup-monorepo.yml`       | changed-project matrix             | `paths-config`, `force-all`, `max-parallel`                                                             | `changed-projects.json`, matrix artifact                          | avoid required-check deadlocks from event-level path filters; prefer always-run aggregator. ([Stack Overflow][13])       |
| `wf-pack-dotnet-nuget.yml` | pack `.nupkg`/`.snupkg`            | `project`, `version`, `configuration`, `include-symbols`                                                | packages, package validation report, attestation                  | deterministic build, SourceLink, no publish.                                                                             |
| `wf-publish-nuget.yml`     | publish packages                   | `source`, `trusted-publishing`, `skip-duplicate`                                                        | package IDs/versions, NuGet URLs                                  | OIDC Trusted Publishing first; API-key fallback. ([Microsoft for Developers][9])                                         |
| `wf-publish-container.yml`   | OCI image publish                  | `context`, `dockerfile`, `image`, `platforms`, `version`, `version-tag`, `dotnet-setversion`, `push`, `cache-mode`, `sbom`, `provenance` | digest, metadata JSON, SBOM, provenance, publish summary          | Docker Buildx, multi-platform, optional .NET version stamping, registry/GHA cache, secret mounts. ([GitHub][14])         |
| `wf-release.yml`           | version/changelog/GitHub Release   | `release-tool`, `package-kind`, `dry-run`                                                               | release notes, tag, release URL                                   | release-please for release PRs; semantic-release for full automation. ([GitHub][15])                                     |
| `wf-deploy-k8s.yml`        | deploy chart/manifests             | `environment`, `namespace`, `image-digest`, `chart`, `values`, `timeout`                                | rollout report, rendered manifests, deploy URL                    | environment protection, OIDC cloud auth, `helm upgrade --install`, rollout status. ([GitHub Docs][16])                   |
| `wf-security-scan.yml`     | code/deps/actions/container checks | `scan-codeql`, `scan-deps`, `scan-container`, `licenses`                                                | SARIF, dep review, vuln report                                    | CodeQL + Dependency Review baseline. ([GitHub][17])                                                                      |
| `wf-required-check.yml`    | required check aggregator          | `required-jobs-json`                                                                                    | single required status                                            | solves skipped path-filter required checks. ([Stack Overflow][13])                                                       |
| `wf-platform-selftest.yml` | test platform repo itself          | `scenario`, `canary-ref`                                                                                | contract test report                                              | run on PR before release tag update.                                                                                     |

## Public contract rules

1. **Inputs over env**. `workflow_call` supports typed `string|number|boolean` inputs; use explicit inputs for every behavior switch. Avoid hidden reliance on caller env. ([GitHub Docs][1])
2. **Named secrets by default**. Use `secrets: inherit` only inside same trusted org. Environment secrets cannot be passed through `workflow_call`; job-level `environment` in called workflow can shadow passed secret. ([GitHub Docs][1])
3. **Permissions explicit, minimal**. Top-level `permissions: {}`; each job grants exact scope. Nested reusable workflows cannot elevate permissions; permissions can only stay same or reduce across chain. ([GitHub Docs][1])
4. **Version refs**. Consumers use stable major tags for workflows/actions by default. Use full SHAs only for high-risk incident response or where a caller policy requires immutable pins. ([GitHub Docs][18])
5. **No dynamic `uses`**. GitHub disallows contexts/expressions in reusable workflow `uses`; use finite dispatch/proxy workflows instead. ([GitHub Docs][1])
6. **No deep nesting**. Max reusable workflow chain depth = 10. Keep platform workflows 1–2 levels max; secrets must be passed hop-by-hop. ([GitHub Docs][1])
7. **Matrix output trap**. Reusable workflow outputs under matrix resolve from last successful completing workflow that sets value; use artifacts/JSON summary for aggregation. ([GitHub Docs][1])
8. **Stable API**. Minor release: add inputs/outputs only. Major release: remove/rename/change semantics. Deprecate via warnings for ≥1 minor cycle.
9. **Dogfood before release**. Platform PR -> selftest fixtures -> canary ref -> immutable release/tag -> SHA catalog update.
10. **Docs generated from schema**. Every workflow input/output has JSON schema + generated Markdown. No schema drift.

## Security baseline

| Area                   | Rule                                                                                                                                                                                                                               |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| External actions       | Prefer official/verified/active repos; audit source; pin SHA; auto-update via controlled PR; never allow unpinned marketplace actions. Full SHA pinning mitigates tag/branch takeover and compromised updates. ([GitHub Docs][18]) |
| Token perms            | `contents: read` default. Add `id-token: write` only for OIDC publish/deploy. Add `packages: write` only for GHCR/package publish. Add `checks/pull-requests: write` only for annotations/comments.                                |
| PRs from forks         | Use `pull_request` for build/test. No secrets. No package publish. No cache writes beyond GitHub policy.                                                                                                                           |
| `pull_request_target`  | Metadata-only. Never checkout/execute fork head with secrets or write token; that pattern enables “pwn request” compromise. ([GitHub Docs][19])                                                                                    |
| `workflow_run`         | Treat upstream artifacts as untrusted unless upstream run came from trusted ref + attested digest. No direct script execution from downloaded artifact.                                                                            |
| Secrets                | Never echo env. Use `::add-mask::` for derived secrets. No secrets in build args; Docker docs recommend BuildKit secret mounts instead. ([Docker Documentation][20])                                                               |
| Cache                  | Lock cache keys to OS/arch/toolchain/lockfile. No broad restore keys for untrusted PRs. Read-only cache behavior now exists for some untrusted triggers, but still design fail-closed. ([The GitHub Blog][5])                      |
| Dependency changes     | Run Dependency Review on PRs; fail on high severity/license policy. ([GitHub Docs][21])                                                                                                                                            |
| Code scanning          | CodeQL for supported langs; upload SARIF. ([GitHub][17])                                                                                                                                                                           |
| Runner hardening       | Optional `harden-runner` for network egress/process/file monitoring, especially on self-host or high-trust jobs. It supports GitHub-hosted and self-hosted runners. ([GitHub][22])                                                 |
| Supply-chain incidents | Treat action ecosystem as hostile-by-default. `tj-actions/changed-files` compromise showed secret leakage risk across many repos; pinning + allowlists + egress policy matter. ([Snyk][23])                                        |

## Observability contract

Every reusable workflow emits:

```text
1. GitHub step summary
2. run-metadata.json
3. artifact-manifest.json
4. tool-versions.txt
5. diagnostics bundle on failure
6. annotations/problem matchers where possible
7. stable outputs for machine consumers
```

Use `$GITHUB_STEP_SUMMARY` for human readout: inputs digest, resolved tool versions, cache hit/miss, test count, coverage, artifact links, package/image refs, deployment target, rollout status, failure hints.

Use log groups for noisy commands; use problem matchers/annotations for compiler, linter, test failures. GitHub workflow commands support grouped logs and annotations; problem matchers scan logs and surface UI annotations. ([GitHub Docs][24])

Failure diagnostics:

| Workflow | Always collect on failure                                                                                          |
| -------- | ------------------------------------------------------------------------------------------------------------------ |
| .NET     | TRX/JUnit, coverage, MSBuild `.binlog`, `dotnet --info`, NuGet sources sans secrets, test blame dumps when enabled |
| Node     | package-manager version, lockfile hash, test reports, coverage, build logs                                         |
| Python   | `python -VV`, package freeze/export, JUnit, coverage, typecheck logs                                               |
| Docker   | Buildx builder inspect, build metadata, SBOM/provenance status, image digest, cache mode                           |
| K8s      | rendered manifests, Helm diff/output, `kubectl describe`, events, rollout status, pod logs for failed pods         |
| Platform | resolved inputs, action SHAs, permissions summary, runner labels, workspace tree limited depth                     |

Artifact manifest shape:

```json
{
  "schema": "ci-artifact-manifest/v1",
  "producer": {
    "repository": "owner/repo",
    "workflow": "wf-setup-dotnet",
    "run_id": "123456789",
    "run_attempt": "1",
    "job": "test"
  },
  "artifacts": [
    {
      "name": "repo-api-tests-linux-x64-abc123",
      "kind": "test-report",
      "artifact_id": "123",
      "artifact_url": "https://github.com/...",
      "artifact_digest": "sha256:...",
      "sha256": "...",
      "retention_days": 14,
      "paths": ["test-results/*.trx"]
    }
  ]
}
```

`upload-artifact` exposes `artifact-id`, `artifact-url`, `artifact-digest`; supports `compression-level`, `overwrite`, `if-no-files-found`, `archive:false`; hidden files are excluded by default from v4.4+. Use these as contract, not incidental behavior. ([GitHub][25])

## Artifact rules

* Naming: `{repo}-{component}-{kind}-{os}-{arch}-{runtime}-{version-or-sha}`.
* Separate **human artifacts** from **machine artifacts**.
* Use `if-no-files-found: error` for release/publish artifacts; `warn` for optional PR diagnostics.
* Use `compression-level: 0` for already-compressed data, large binaries, coverage HTML bundles when speed matters. ([GitHub][25])
* Use `archive:false` for single Markdown/HTML/image/log intended for direct browser view. ([The GitHub Blog][6])
* Never upload hidden files unless allowlisted; hidden-file default is exclude. ([GitHub][26])
* Retention: PR 7–14d; main 30–60d; release assets via GitHub Release/package registry/container registry.
* Release artifacts immutable, attested, digest-addressed.

## Speed + parallelism

Use 3 layers:

1. **Workflow graph parallelism**: split lint/test/build/package/security jobs.
2. **Matrix parallelism**: OS/runtime/project/test shard. Use `max-parallel` to match runner pool capacity. GitHub matrix supports `max-parallel`. ([GitHub Docs][27])
3. **Step-level parallelism**: same-runner independent commands via `parallel`, `background`, `wait`, `wait-all`. Best for service startup, telemetry, frontend/backend build sharing same checkout. Not valid inside composite actions. ([The GitHub Blog][4])

Caching:

* Use `setup-dotnet`, `setup-node`, `setup-python` built-in dependency caching where it fits. ([GitHub][10])
* Cache dependencies, not final build outputs, unless deterministic + invalidation exact.
* Cache key = `{os}-{arch}-{toolchain-version}-{lockfile-hash}-{config-hash}`.
* Docker: Buildx cache via GHA or registry; registry cache better across self-host/ARC pools. Buildx supports multi-platform, secrets, remote cache, BuildKit features. ([GitHub][14])
* Monorepo: detect changed projects -> dynamic matrix; required check aggregator always runs to avoid skipped required checks. ([GitHub][28])

## Runner/self-host/ARC contract

Workflow inputs must accept runner labels as JSON:

```yaml
with:
  runs-on-json: '["self-hosted","linux","x64","dotnet","arc"]'
```

Rules:

* No hardcoded `ubuntu-latest` in core workflows except default.
* Assume self-host runner may lack Docker, sudo, preinstalled SDKs, network egress.
* Use `$RUNNER_TEMP`, `$GITHUB_WORKSPACE`, `$HOME/.cache`; never write elsewhere.
* Cleanup always: Docker builders, temp files, kube contexts, credentials.
* Prefer ephemeral runners; no shared workspace between jobs.
* Pin tools through setup actions/global config; no ambient SDK assumptions.
* Add runner preflight step: OS, arch, runner version, disk, Docker/Buildx availability, K8s tools, free space.
* ARC runner scale sets = preferred K8s-native self-host model; ARC is Kubernetes operator/controller for scaling GitHub Actions runners. ([GitHub Docs][29])
* Track ARC features: runner scale set labels, resource customization, listener scheduling landed in ARC GA-era updates. ([The GitHub Blog][30])
* Maintain runner image rollout cadence below GitHub 30-day freshness req. ([The GitHub Blog][3])

Runner label policy:

```yaml
labels:
  github-hosted:
    - ubuntu-latest
  self-hosted-linux:
    - self-hosted
    - linux
    - x64
  arc-dotnet:
    - self-hosted
    - linux
    - x64
    - arc
    - dotnet
  arc-docker:
    - self-hosted
    - linux
    - x64
    - arc
    - docker
```

## Kubernetes deploy contract

Deploy workflow should require immutable image ref:

```text
image: ghcr.io/org/app@sha256:<digest>
chart: app-1.2.3.tgz
environment: prod
namespace: app-prod
```

Rules:

* Use GitHub Environments for approvals, branch restrictions, wait timers, custom protection gates. ([GitHub Docs][16])
* Use OIDC cloud auth where possible; Azure Login supports OIDC and recommends it over secret-based auth. ([GitHub][31])
* Render manifests before apply; upload rendered manifests as artifact.
* `helm upgrade --install --atomic --wait --timeout ...`
* Capture `kubectl rollout status`; on fail capture events, describe, pod logs.
* No kubeconfig secrets in repo/org if cloud OIDC can mint short-lived creds.
* Separate deploy approval from build. Build produces digest; deploy consumes digest.
* Use concurrency group per env/app: `deploy-${{ inputs.environment }}-${{ inputs.app }}` with ordered queue.
* Optional chart publishing via GitHub Pages using Helm chart-releaser action for chart repos. ([GitHub][32])

## Release + publishing model

Default: **release-please** for multi-project/.NET repos, because release PR gives reviewable changelog/version bump. Use **semantic-release** for repos wanting full automation from Conventional Commits through publish. release-please parses Conventional Commits and creates release PRs/changelogs; semantic-release automates version, notes, publish pipeline. ([GitHub][15])

NuGet:

* Build/package in one workflow; publish in separate privileged workflow.
* Use `id-token: write` only for Trusted Publishing.
* Publish `.nupkg` + `.snupkg`.
* Use `--skip-duplicate` for idempotent retries.
* Generate package manifest: package ID, version, SHA256, source commit, publish URL.
* Fallback API key secret scoped to package/feed only.

Containers:

* Build via Docker Buildx.
* Tags: `sha-<short>`, semver, branch; `latest` only for default stable branch.
* Publish digest output; deployments consume digest, not tag.
* Enable SBOM/provenance; Docker docs note provenance defaults vary by public/private repo, and attestations are not produced with `load:true`/Docker exporter. ([Docker Documentation][20])
* Login via registry-specific action/OIDC/token; Docker Hub recommends PAT, not password. ([GitHub][33])

GitHub releases:

* Prefer immutable releases.
* Attach only final artifacts + manifest + provenance.
* Never replace release asset; create patch release.
* Use attestations for binaries/container images where supported. ([GitHub Docs][34])

## Recommended public actions/plugins

| Category       | Preferred                                                                                                                                                         |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Official setup | `actions/checkout`, `actions/setup-dotnet`, `actions/setup-node`, `actions/setup-python`, `actions/upload-artifact`, `actions/download-artifact`; use `runs-on/cache` for dependency cache |
| Security       | `github/codeql-action`, `actions/dependency-review-action`, `actions/attest-build-provenance`, StepSecurity `harden-runner`                                       |
| Docker         | `docker/setup-buildx-action`, `docker/login-action`, `docker/build-push-action`, `docker/metadata-action`                                                         |
| Release        | `googleapis/release-please-action`, `semantic-release`                                                                                                            |
| Helm           | `helm/chart-releaser-action`                                                                                                                                      |
| Monorepo paths | `dorny/paths-filter` or internal path detector; avoid any unpinned changed-files action after 2025 compromise class                                               |

Docker maintains reusable workflow repos as precedent for central workflow products with security/perf/reliability defaults; GitHub also maintains starter workflow repos as reference patterns, but starter workflows are templates, not runtime contracts. ([GitHub][35])

## Consumer usage pattern

```yaml
name: ci

on:
  pull_request:
  push:
    branches: [main]

permissions: {}

jobs:
  dotnet:
    name: .NET setup
    uses: org/gha-platform/.github/workflows/wf-setup-dotnet.yml@v1
    permissions:
      contents: read
      checks: write
      pull-requests: write
    with:
      dotnet-version: "10.0.x"
      solution: "src/App.sln"
      configuration: "Release"
      runs-on-json: '["ubuntu-latest"]'
      runs-on-self-hosted: false
      enable-cache: true
      coverage: true
```

Publish pattern:

```yaml
name: publish

on:
  release:
    types: [published]

permissions: {}

jobs:
  nuget:
    name: NuGet publish
    uses: org/gha-platform/.github/workflows/wf-publish-nuget.yml@v1
    permissions:
      contents: read
      id-token: write
    with:
      source: "nuget.org"
      trusted-publishing: true
      enable-cache: true
      artifact-name: "nuget-packages"
```

Container pattern:

```yaml
name: image

on:
  push:
    branches: [main]

permissions: {}

jobs:
  image:
    name: Container publish
    uses: org/gha-platform/.github/workflows/wf-publish-container.yml@v1
    permissions:
      contents: read
      packages: write
      id-token: write
      attestations: write
    with:
      image: "ghcr.io/org/app"
      context: "."
      dockerfile: "Dockerfile"
      platforms: "linux/amd64,linux/arm64"
      version: ${{ needs.release.outputs.new-version }}
      version-tag: ${{ needs.release.outputs.new-tag }}
      dotnet-setversion: true
      push: true
      sbom: true
      provenance: "mode=max"
```

## Reusable workflow skeleton

```yaml
name: wf-setup-dotnet

on:
  workflow_call:
    inputs:
      dotnet-version:
        type: string
        required: false
        default: "10.0.x"
      solution:
        type: string
        required: true
      configuration:
        type: string
        required: false
        default: "Release"
      runs-on-json:
        type: string
        required: false
        default: '["ubuntu-latest"]'
      timeout-minutes:
        type: number
        required: false
        default: 30
      coverage:
        type: boolean
        required: false
        default: true
    outputs:
      artifact-manifest:
        description: "Artifact manifest JSON path"
        value: ${{ jobs.test.outputs.artifact-manifest }}

permissions: {}

jobs:
  test:
    runs-on: ${{ fromJSON(inputs.runs-on-json) }}
    timeout-minutes: ${{ inputs.timeout-minutes }}

    permissions:
      contents: read
      checks: write

    outputs:
      artifact-manifest: ${{ steps.manifest.outputs.path }}

    steps:
      - name: Checkout
        uses: actions/checkout@v7
        with:
          persist-credentials: false

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: ${{ inputs.dotnet-version }}

      - name: Cache NuGet packages
        if: inputs.enable-cache
        uses: runs-on/cache@v5
        with:
          path: ${{ github.workspace }}/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/packages.lock.json', '.config/dotnet-tools.json') }}
          restore-keys: |
            ${{ runner.os }}-nuget-

      - name: Tool versions
        shell: bash
        run: |
          dotnet --info
          mkdir -p artifacts/meta
          dotnet --info > artifacts/meta/dotnet-info.txt

      - name: Restore
        shell: bash
        run: dotnet restore "${{ inputs.solution }}" --locked-mode

      - name: Build
        shell: bash
        run: dotnet build "${{ inputs.solution }}" --configuration "${{ inputs.configuration }}" --no-restore -p:ContinuousIntegrationBuild=true -bl:artifacts/msbuild/build.binlog

      - name: Test
        shell: bash
        run: dotnet test "${{ inputs.solution }}" --configuration "${{ inputs.configuration }}" --no-build --logger "trx;LogFileName=test.trx" --results-directory artifacts/test-results

      - name: Build artifact manifest
        id: manifest
        if: always()
        shell: bash
        run: |
          echo '{"schema":"ci-artifact-manifest/v1"}' > artifacts/artifact-manifest.json
          echo "path=artifacts/artifact-manifest.json" >> "$GITHUB_OUTPUT"

      - name: Upload diagnostics
        if: always()
        uses: actions/upload-artifact@v7
        with:
          name: dotnet-ci-${{ github.run_id }}-${{ github.run_attempt }}
          path: artifacts
          if-no-files-found: error
          retention-days: 14
```

## Monorepo rules

* Do not rely solely on `on.paths` for required checks; skipped workflows can block merge. Use always-running workflow + internal changed-path detection + required-check aggregator. ([Stack Overflow][13])
* Produce `changed-projects.json`:

  * `project`
  * `language`
  * `path`
  * `test-command`
  * `dockerfile`
  * `package-manifest`
  * `deploy-target`
* Matrix from JSON; cap with `max-parallel`.
* Allow `force-all` input for manual/scheduled full CI.
* Never evaluate untrusted file paths as shell code; pass via JSON, quote every path.

## Edge-case checklist

| Edge case                                 | Rule                                                                                                             |
| ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| Caller env not available                  | Pass via `with`; keep env local.                                                                                 |
| Environment secrets in reusable workflow  | Avoid unless central workflow owns env; environment secrets do not pass via `workflow_call`. ([GitHub Docs][1])  |
| Nested workflow secrets                   | Pass explicitly each hop; avoid nesting deeper than 2. ([GitHub Docs][1])                                        |
| Matrix outputs                            | Do not aggregate via reusable outputs; upload JSON artifacts. ([GitHub Docs][1])                                 |
| Dynamic reusable workflow ref             | Not supported; generate finite wrapper workflows. ([GitHub Docs][1])                                             |
| Composite action wants multiple jobs      | Wrong primitive; use reusable workflow. ([GitHub Docs][36])                                                      |
| Composite action wants secrets            | Prefer reusable workflow; composites do not get secrets as first-class contract. ([GitHub Docs][36])             |
| Parallel work inside composite            | Not supported by current parallel/background step feature. ([GitHub Docs][37])                                   |
| Docker attestations missing               | Check `load:true`/Docker exporter; use push/export mode that supports attestations. ([Docker Documentation][20]) |
| Hidden files missing from artifact        | Expected default; allowlist explicitly. ([GitHub][26])                                                           |
| Self-host action fails after runtime bump | Update runner + action catalog to Node24-compatible majors. ([The GitHub Blog][2])                               |
| PR target needs label/comment             | Do metadata-only; no fork checkout/execution. ([GitHub Docs][19])                                                |

## Agentic development rules

1. **Read contract first**: workflow schema, docs, examples, tests. No blind YAML edits.
2. **Preserve API**: no input/output rename/removal outside major.
3. **Threat-model trigger**: `pull_request`, `pull_request_target`, `workflow_run`, `issue_comment`, `release`, `schedule`, `workflow_dispatch`.
4. **Pin every action**: update action catalog through bot PR + selftest, not ad hoc.
5. **Minimize perms**: start `permissions: {}`; add per job only.
6. **Separate trust zones**: build/test untrusted; publish/deploy trusted. Artifacts crossing zone need digest/attestation.
7. **Fail closed for release**: missing package/image/provenance -> fail.
8. **Fail useful for PR**: upload diagnostics even when test/build fails.
9. **Quote all shell vars**: user-controlled contexts go to env first, then quoted in shell.
10. **No secret-shaped outputs**: never write tokens, kubeconfigs, connection strings to outputs/artifacts/cache.
11. **No ambient tool req**: setup every tool or preflight runner capability.
12. **Self-host safe**: no sudo/Docker/socket assumptions; no stateful workspace assumptions.
13. **K8s deploy immutable**: digest/chart version only; no mutable image tag deploy to prod.
14. **Observe by default**: summary, manifest, metadata, diagnostics.
15. **Optimize after correctness**: lockfile restore, deterministic build, exact cache keys, parallelism capped to runner pool.
16. **Test fixture matrix**: .NET lib, ASP.NET image, Node pnpm, Python, mixed monorepo, K8s dry-run.
17. **Canary release**: platform repo dogfoods `main`; consumers use release SHA.
18. **Document every edge**: changed behavior -> docs + ADR + migration note.
19. **Prefer deletion over flags**: if option unused, remove in next major; avoid combinatorial YAML.
20. **No “magic” defaults**: every default visible in schema + docs.

## Required docs

| File                       | Content                                                                              |
| -------------------------- | ------------------------------------------------------------------------------------ |
| `README.md`                | scope, supported project types, quick copy-paste examples                            |
| `docs/workflow-catalog.md` | workflow list, inputs, outputs, permissions, secrets, artifacts                      |
| `docs/input-contracts.md`  | generated from schemas; breaking-change policy                                       |
| `docs/security-model.md`   | trust zones, event risks, pinning, tokens, secrets, PR/fork policy                   |
| `docs/permissions.md`      | per-workflow minimal permissions table                                               |
| `docs/artifacts.md`        | naming, retention, manifest schema, digest policy, browseable artifacts              |
| `docs/observability.md`    | summaries, logs, annotations, diagnostics, debug flags                               |
| `docs/caching.md`          | cache keys, invalidation, untrusted cache, Docker cache                              |
| `docs/runner-contract.md`  | hosted/self-host/ARC reqs, labels, preflight, cleanup                                |
| `docs/arc-kubernetes.md`   | ARC scale sets, runner image contract, K8s constraints                               |
| `docs/releases.md`         | release-please vs semantic-release, channels, immutable release process              |
| `docs/nuget.md`            | pack/publish, Trusted Publishing, fallback secrets                                   |
| `docs/containers.md`       | Buildx, tags, digest, SBOM, provenance, cache, registry auth                         |
| `docs/deployments.md`      | env protection, OIDC, Helm/Kubectl, rollback, concurrency                            |
| `docs/monorepos.md`        | changed detection, required checks, matrix generation                                |
| `docs/troubleshooting.md`  | common failures, debug toggles, runner issues, cache/artifact issues                 |
| `docs/edge-cases.md`       | `pull_request_target`, nested workflows, matrix outputs, env secrets, dynamic `uses` |

## Operating model

* `main`: development + dogfood only.
* `v1`, `v2`: stable major refs.
* Release tag: immutable, signed/attested where possible.
* Consumer repos pin SHA from release catalog.
* Renovate opens platform action-update PRs.
* Platform CI validates:

  * YAML syntax
  * workflow schema
  * pinned actions
  * permissions policy
  * fixture consumers
  * docs generated/no diff
  * sample artifact manifests
  * self-host/ARC preflight where available

Best end state: consumer repos contain tiny wrappers; platform repo owns behavior, docs, observability, security posture, release cadence.

[1]: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows "Reuse workflows - GitHub Docs"
[2]: https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/?utm_source=chatgpt.com "Deprecation of Node 20 on GitHub Actions runners"
[3]: https://github.blog/changelog/2026-06-12-github-actions-minimum-version-enforcement-timeline-for-self-hosted-runners/ "GitHub Actions: Minimum version enforcement timeline for self-hosted runners - GitHub Changelog"
[4]: https://github.blog/changelog/2026-06-25-actions-steps-can-now-be-run-in-parallel/ "Actions steps can now be run in parallel - GitHub Changelog"
[5]: https://github.blog/changelog/2026-06-26-read-only-actions-cache-for-untrusted-triggers/ "Read-only Actions cache for untrusted triggers - GitHub Changelog"
[6]: https://github.blog/changelog/2026-02-26-github-actions-now-supports-uploading-and-downloading-non-zipped-artifacts/ "GitHub Actions now supports uploading and downloading non-zipped artifacts - GitHub Changelog"
[7]: https://github.blog/changelog/2026-05-07-github-actions-concurrency-groups-now-allow-larger-queues/ "GitHub Actions concurrency groups now allow larger queues - GitHub Changelog"
[8]: https://github.blog/changelog/2025-10-28-immutable-releases-are-now-generally-available/?utm_source=chatgpt.com "Immutable releases are now generally available"
[9]: https://devblogs.microsoft.com/dotnet/enhanced-security-is-here-with-the-new-trust-publishing-on-nuget-org/?utm_source=chatgpt.com "New Trusted Publishing enhances security on NuGet.org"
[10]: https://github.com/actions/setup-dotnet?utm_source=chatgpt.com "actions/setup-dotnet: Set up your GitHub Actions workflow ..."
[11]: https://github.com/actions/setup-node?utm_source=chatgpt.com "GitHub - actions/setup-node: Set up your ..."
[12]: https://github.com/actions/setup-python?utm_source=chatgpt.com "actions/setup-python: Set up your ..."
[13]: https://stackoverflow.com/questions/69348532/github-actions-required-status-check-doesnt-run-due-to-files-in-paths-not-chan?utm_source=chatgpt.com "Github Actions: Required status check doesn't run due to ..."
[14]: https://github.com/docker/build-push-action?utm_source=chatgpt.com "GitHub Action to build and push Docker images with Buildx"
[15]: https://github.com/googleapis/release-please-action?utm_source=chatgpt.com "googleapis/release-please-action"
[16]: https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments?utm_source=chatgpt.com "Deployments and environments"
[17]: https://github.com/github/codeql-action?utm_source=chatgpt.com "Actions for running CodeQL analysis"
[18]: https://docs.github.com/en/actions/reference/security/secure-use "Secure use reference - GitHub Docs"
[19]: https://docs.github.com/en/actions/reference/security/securely-using-pull_request_target "Securely using pull_request_target - GitHub Docs"
[20]: https://docs.docker.com/build/ci/github-actions/attestations/?utm_source=chatgpt.com "Add SBOM and provenance attestations with GitHub Actions"
[21]: https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/manage-your-dependency-security/configure-dependency-review-action?utm_source=chatgpt.com "Configuring the dependency review action"
[22]: https://github.com/step-security/harden-runner?utm_source=chatgpt.com "step-security/harden-runner: Harden-Runner is a CI/ ..."
[23]: https://snyk.io/blog/reconstructing-tj-actions-changed-files-github-actions-compromise/?utm_source=chatgpt.com "Reconstructing the TJ Actions Changed Files GitHub ..."
[24]: https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands?utm_source=chatgpt.com "Workflow commands for GitHub Actions"
[25]: https://github.com/actions/upload-artifact "GitHub - actions/upload-artifact · GitHub"
[26]: https://github.com/actions/upload-artifact/blob/main/docs/MIGRATION.md "upload-artifact/docs/MIGRATION.md at main · actions/upload-artifact · GitHub"
[27]: https://docs.github.com/actions/writing-workflows/choosing-what-your-workflow-does/running-variations-of-jobs-in-a-workflow?utm_source=chatgpt.com "Running variations of jobs in a workflow"
[28]: https://github.com/dorny/paths-filter?utm_source=chatgpt.com "dorny/paths-filter: Conditionally run actions based on files ..."
[29]: https://docs.github.com/en/actions/how-tos/manage-runners/use-actions-runner-controller/deploy-runner-scale-sets?utm_source=chatgpt.com "Deploying runner scale sets with Actions Runner Controller"
[30]: https://github.blog/changelog/2026-03-19-actions-runner-controller-release-0-14-0/?utm_source=chatgpt.com "Actions Runner Controller release 0.14.0"
[31]: https://github.com/marketplace/actions/azure-login?utm_source=chatgpt.com "Azure Login · Actions · GitHub Marketplace"
[32]: https://github.com/helm/chart-releaser-action?utm_source=chatgpt.com "helm/chart-releaser-action"
[33]: https://github.com/docker/login-action?utm_source=chatgpt.com "GitHub Action to login against a Docker registry"
[34]: https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases?utm_source=chatgpt.com "Immutable releases - GitHub Docs"
[35]: https://github.com/docker/github-builder?utm_source=chatgpt.com "docker/github-builder: Official Docker-maintained reusable ..."
[36]: https://docs.github.com/en/actions/concepts/workflows-and-actions/reusing-workflow-configurations "Reusing workflow configurations - GitHub Docs"
[37]: https://docs.github.com/actions/using-workflows/workflow-syntax-for-github-actions "Workflow syntax for GitHub Actions - GitHub Docs"
