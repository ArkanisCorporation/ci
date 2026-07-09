# Private NuGet Credentials For Shared .NET Workflows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow shared .NET workflows to restore, build, test, pack, containerize, and deploy projects that depend on private NuGet feeds without leaking credentials into logs, caches, artifacts, images, or workflow summaries.

**Architecture:** Add an explicit private NuGet restore credential contract, backed by one caller-provided `NUGET_AUTH_JSON` secret.
Host-side .NET restore and tool restore use NuGet's `NuGetPackageSourceCredentials_{name}` environment variable convention.
`NUGET_AUTH_JSON` values can be literal strings, `op://` 1Password secret references, or `github://actor` and `github://token` runtime references.
Docker Buildx workflows optionally generate a temporary `NuGet.Config` and pass it as a BuildKit secret file, because credentials needed inside a Dockerfile must not be sent as build args or copied into image layers.

**Tech Stack:** GitHub Actions reusable workflows, composite actions, Bash, .NET file-based scripts, NuGet configuration, 1Password `load-secrets-action`, Docker Buildx BuildKit secrets, existing schema and workflow validation scripts.

---

## Current Findings

The existing shared restore path is split across `.github/actions/setup-dotnet/action.yml`, `.github/actions/dotnet-pack-nuget/action.yml`, `.github/workflows/wf-setup-dotnet-generated-code.yml`, the Aspire deploy workflows, and the Docker Buildx container workflows.

The normal test and format workflows already centralize restore through `.github/actions/setup-dotnet/action.yml`.

The NuGet pack workflows restore through `.github/actions/dotnet-pack-nuget/action.yml`.

The generated-code and Aspire workflows currently call `actions/setup-dotnet@v5`, `dotnet tool restore`, and restore or deploy commands inline.

The container workflows do not run `dotnet restore` on the host, but their Dockerfiles often do restore inside the image build.

The current `.github/actions/setup-dotnet/AGENTS.md` rule says not to mutate NuGet sources with secrets unless there is explicit publish context.

This plan changes that rule to allow explicit private restore context while keeping secrets out of repository files and artifacts.

## Upstream Research

GitHub reusable workflows accept named secrets via `jobs.<job_id>.secrets`, and same-organization callers may use `secrets: inherit`.

The safer platform contract should prefer one explicitly named secret over `secrets: inherit`, because inheritance passes more secrets than this workflow needs.

GitHub context availability allows `secrets` in `jobs.<job_id>.secrets.<secret_id>` and step-level `env` or `with`, but not in reusable workflow `jobs.<job_id>.with.<input_id>`.

That means credential values must be passed through `secrets`, not through `with` inputs.

GitHub secrets cannot be referenced directly in `if:` conditionals.

Workflow examples should derive non-secret presence flags through `env` and then branch on the `env` context.

NuGet supports multiple package sources and separate `packageSourceCredentials` entries.

NuGet also supports `NuGetPackageSourceCredentials_{name}` environment variables with values like `Username=<user>;Password=<token>;ValidAuthenticationTypes=Basic`.

NuGet warns that clear text credentials in committed `NuGet.Config` files are risky.

NuGet recommends repository-level `NuGet.Config` files for repeatable non-secret source configuration, often with `<clear />`.

NuGet also warns about dependency confusion when adding multiple sources, so caller repositories should use package source mapping for private package prefixes.

`actions/setup-dotnet@v5` supports one `source-url` authenticated by `NUGET_AUTH_TOKEN`, but its action contract is single-source oriented and there is an open upstream feature request for multiple package sources.

Docker Buildx supports `secret-files`, and Docker's docs specifically recommend mounting generated credential files as build secrets for package manager configs.

1Password's official GitHub Action can load `op://vault/item/field` secret references into GitHub Actions environment variables with a service account token or Connect server.

This plan scopes the first implementation to service account tokens because the user asked for a CI-provided 1Password secret.

The workflow must never invoke the `op` CLI directly.

Sources checked:

- GitHub reusable workflow secrets: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub Actions secrets in conditionals: https://docs.github.com/actions/security-guides/using-secrets-in-github-actions
- GitHub Actions context availability: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- NuGet authenticated feeds: https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds
- NuGet config reference: https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file
- `dotnet nuget add source`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-add-source
- `dotnet tool restore --configfile`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-restore
- `actions/setup-dotnet@v5`: https://github.com/actions/setup-dotnet
- `actions/setup-dotnet` multiple source request: https://github.com/actions/setup-dotnet/issues/167
- Docker Buildx secrets: https://docs.docker.com/build/ci/github-actions/secrets/
- `docker/build-push-action@v7` inputs: https://github.com/docker/build-push-action
- 1Password GitHub Actions integration: https://www.1password.dev/ci-cd/github-actions
- 1Password `load-secrets-action`: https://github.com/1Password/load-secrets-action

## Recommended Contract

Add one optional reusable workflow secret named `NUGET_AUTH_JSON` to every shared .NET workflow that can need private restore credentials.

The secret is optional by default, so current public and unauthenticated consumers keep working.

Add one optional reusable workflow secret named `OP_SERVICE_ACCOUNT_TOKEN` to the same workflows.

This secret is only required when `NUGET_AUTH_JSON` contains one or more string values that start with `op://`.

If a caller provides `NUGET_AUTH_JSON`, workflows configure NuGet credentials before `dotnet tool restore`, `dotnet restore`, `dotnet pack`, `dotnet build`, `dotnet test`, `dotnet format`, CleanupCode, Aspire deploy, or Docker Buildx.

The secret value is a compact JSON document.

The JSON document supports multiple credentials by using an array under `sources`.

Each source object has these fields:

- `name`: Required.
  This must match the package source key in the caller's committed `NuGet.Config`.
- `source`: Optional for host restore, required for Docker Buildx secret config generation.
  This is the feed URL written into temporary generated `NuGet.Config` files.
- `username`: Required.
  This can be a literal string, an `op://` reference, or `github://actor`.
- `password`: Required.
  This can be a literal string, an `op://` reference, or `github://token`.
- `validAuthenticationTypes`: Optional.
  Use `Basic` for PAT-style feeds such as GitHub Packages and many Azure Artifacts setups.
- `protocolVersion`: Optional.
  Use `"3"` for v3 feeds.

Concrete mixed secret value:

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
    },
    {
      "name": "vendor",
      "source": "https://vendor.example.com/nuget/v3/index.json",
      "username": "ci",
      "password": "LITERAL_TOKEN_VALUE_STORED_INSIDE_NUGET_AUTH_JSON",
      "validAuthenticationTypes": "Basic",
      "protocolVersion": "3"
    }
  ]
}
```

The `name` must match the caller repository's `NuGet.Config` package source key.

The `name` should be restricted to environment-variable-safe characters such as letters, digits, and underscores.

The `source` is required when Docker Buildx secret file generation is enabled.

The `password` is the token or PAT used by the private NuGet store after resolution.

`github://actor` resolves to the current workflow run's `github.actor`.

`github://token` resolves to the current workflow run's `github.token`.

Workflows must pass that token to the auth setup step explicitly as an environment variable such as `GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}` because it is not available to shell code as plain `$GITHUB_TOKEN` unless the workflow exposes it.

The token must be masked before composing the NuGet credential string.

For GitHub Packages, use `GITHUB_TOKEN` only when the package access model allows that repository to read the package.

For packages associated with other private repositories, GitHub's docs say to use a classic PAT with at least `read:packages`, unless package access has been granted to the workflow repository.

`op://` values are resolved by generating a temporary 1Password env file with stable variable names, loading it with `1password/load-secrets-action@v4`, and then applying the loaded values to NuGet.

Example generated 1Password env file:

```dotenv
NUGET_AUTH_OP_S1_USERNAME=op://ci-nuget/internal-feed/username
NUGET_AUTH_OP_S1_PASSWORD=op://ci-nuget/internal-feed/token
```

The generated env file must live under `RUNNER_TEMP`.

The generated env file must be deleted in an `if: always()` cleanup step.

The generated env file must never be uploaded as an artifact.

## Caller Repository Shape

The caller should commit a non-secret `NuGet.Config`.

Example:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="github" value="https://nuget.pkg.github.com/ArkanisCorporation/index.json" protocolVersion="3" />
    <add key="internal" value="https://nuget.example.com/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="github">
      <package pattern="Arkanis.*" />
    </packageSource>
    <packageSource key="internal">
      <package pattern="Company.Internal.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

The caller workflow passes the secret explicitly.

Example:

```yaml
jobs:
  dotnet-test:
    uses: ArkanisCorporation/ci/.github/workflows/wf-dotnet-test.yml@v1
    permissions:
      contents: read
      pull-requests: write
    with:
      solution: src/MyProduct.slnx
      dotnet-version: 10.0.x
    secrets:
      NUGET_AUTH_JSON: ${{ secrets.ARKANIS_NUGET_AUTH_JSON }}
      OP_SERVICE_ACCOUNT_TOKEN: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}
```

Multiple credentials are provided by adding more entries to the `sources` array.

This avoids needing `NUGET_TOKEN_1`, `NUGET_TOKEN_2`, and similar fixed secret slots in every reusable workflow contract.

## Concrete Host Restore Shape

The final workflow should hide this behind `.github/actions/setup-nuget-auth`, but the generated host-side shape should be equivalent to this:

```yaml
on:
  workflow_call:
    inputs:
      solution:
        type: string
        required: true
      nuget-config-file:
        type: string
        required: false
        default: NuGet.Config
    secrets:
      NUGET_AUTH_JSON:
        required: false
      OP_SERVICE_ACCOUNT_TOKEN:
        required: false

jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    env:
      NUGET_PACKAGES: ${{ github.workspace }}/.nuget/packages
      SOLUTION: ${{ inputs.solution }}
      NUGET_CONFIG: ${{ github.workspace }}/${{ inputs.nuget-config-file }}
      NUGET_AUTH_JSON_PRESENT: ${{ secrets.NUGET_AUTH_JSON != '' }}
      OP_SERVICE_ACCOUNT_TOKEN_PRESENT: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN != '' }}

    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false

      - uses: actions/setup-dotnet@v5
        with:
          dotnet-version: 10.0.x

      - name: Prepare NuGet auth references
        id: nuget-auth-prepare
        if: env.NUGET_AUTH_JSON_PRESENT == 'true'
        uses: ./.ci/arkanis-ci/.github/actions/setup-nuget-auth
        env:
          GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}
        with:
          nuget-auth-json: ${{ secrets.NUGET_AUTH_JSON }}
          phase: prepare
          op-env-file: ${{ runner.temp }}/nuget-auth/op.env
          op-map-file: ${{ runner.temp }}/nuget-auth/op-map.json

      - name: Require 1Password token
        if: steps.nuget-auth-prepare.outputs.op-required == 'true' && env.OP_SERVICE_ACCOUNT_TOKEN_PRESENT != 'true'
        run: |
          echo "::error::OP_SERVICE_ACCOUNT_TOKEN is required when NUGET_AUTH_JSON contains op:// references."
          exit 1

      - name: Load NuGet auth values from 1Password
        if: steps.nuget-auth-prepare.outputs.op-required == 'true'
        uses: 1password/load-secrets-action@v4
        with:
          export-env: true
        env:
          OP_SERVICE_ACCOUNT_TOKEN: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}
          OP_ENV_FILE: ${{ steps.nuget-auth-prepare.outputs.op-env-file }}

      - name: Apply NuGet auth credentials
        id: nuget-auth
        if: env.NUGET_AUTH_JSON_PRESENT == 'true'
        uses: ./.ci/arkanis-ci/.github/actions/setup-nuget-auth
        env:
          GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}
        with:
          nuget-auth-json: ${{ secrets.NUGET_AUTH_JSON }}
          phase: apply
          credential-mode: env
          op-map-file: ${{ steps.nuget-auth-prepare.outputs.op-map-file }}

      - name: Restore local .NET tools
        run: dotnet tool restore --configfile "$NUGET_CONFIG"

      - name: Restore solution
        run: dotnet restore "$SOLUTION" --locked-mode --configfile "$NUGET_CONFIG"

      - name: Clear temporary NuGet auth values
        if: always() && steps.nuget-auth.outputs.configured == 'true'
        uses: ./.ci/arkanis-ci/.github/actions/setup-nuget-auth
        with:
          phase: cleanup
          op-env-file: ${{ steps.nuget-auth-prepare.outputs.op-env-file }}
          op-map-file: ${{ steps.nuget-auth-prepare.outputs.op-map-file }}
          configured-source-names: ${{ steps.nuget-auth.outputs.source-names }}
```

The `Apply NuGet auth credentials` step writes environment entries equivalent to:

```bash
NuGetPackageSourceCredentials_github="Username=<github.actor>;Password=<github.token>;ValidAuthenticationTypes=Basic"
NuGetPackageSourceCredentials_internal="Username=<1Password username>;Password=<1Password token>;ValidAuthenticationTypes=Basic"
NuGetPackageSourceCredentials_vendor="Username=ci;Password=<literal token>;ValidAuthenticationTypes=Basic"
```

The restore command itself stays ordinary:

```bash
dotnet tool restore --configfile "$NUGET_CONFIG"
dotnet restore "$SOLUTION" --locked-mode --configfile "$NUGET_CONFIG"
```

The restore command does not receive the secret values as arguments.

NuGet discovers them from the `NuGetPackageSourceCredentials_{name}` variables.

## Dockerfile Shape

Container consumers must opt in because the Dockerfile has to mount the BuildKit secret.

The workflow should expose a boolean input such as `nuget-build-secret` with default `false`.

When enabled, the workflow writes a temporary `NuGet.Config` under `RUNNER_TEMP` and passes it to `docker/build-push-action@v7` through `secret-files`.

Example workflow call:

```yaml
jobs:
  container:
    uses: ArkanisCorporation/ci/.github/workflows/wf-publish-container-dotnet.yml@v1
    permissions:
      contents: read
      packages: write
      id-token: write
      attestations: write
    with:
      image: ghcr.io/arkaniscorporation/my-service
      version: 1.2.3
      nuget-build-secret: true
    secrets:
      REGISTRY_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      NUGET_AUTH_JSON: ${{ secrets.ARKANIS_NUGET_AUTH_JSON }}
      OP_SERVICE_ACCOUNT_TOKEN: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}
```

Concrete Docker Buildx auth shape inside the job:

```yaml
env:
  NUGET_AUTH_JSON_PRESENT: ${{ secrets.NUGET_AUTH_JSON != '' }}
  OP_SERVICE_ACCOUNT_TOKEN_PRESENT: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN != '' }}

steps:
  - name: Prepare NuGet auth references
    id: nuget-auth-prepare
    if: inputs.nuget-build-secret
    uses: ./.ci/arkanis-ci/.github/actions/setup-nuget-auth
    env:
      GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}
    with:
      nuget-auth-json: ${{ secrets.NUGET_AUTH_JSON }}
      phase: prepare
      op-env-file: ${{ runner.temp }}/nuget-auth/op.env
      op-map-file: ${{ runner.temp }}/nuget-auth/op-map.json

  - name: Require 1Password token
    if: steps.nuget-auth-prepare.outputs.op-required == 'true' && env.OP_SERVICE_ACCOUNT_TOKEN_PRESENT != 'true'
    run: |
      echo "::error::OP_SERVICE_ACCOUNT_TOKEN is required when NUGET_AUTH_JSON contains op:// references."
      exit 1

  - name: Load NuGet auth values from 1Password
    if: steps.nuget-auth-prepare.outputs.op-required == 'true'
    uses: 1password/load-secrets-action@v4
    with:
      export-env: true
    env:
      OP_SERVICE_ACCOUNT_TOKEN: ${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}
      OP_ENV_FILE: ${{ steps.nuget-auth-prepare.outputs.op-env-file }}

  - name: Generate NuGet BuildKit secret config
    id: nuget-auth
    if: inputs.nuget-build-secret
    uses: ./.ci/arkanis-ci/.github/actions/setup-nuget-auth
    env:
      GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}
    with:
      nuget-auth-json: ${{ secrets.NUGET_AUTH_JSON }}
      phase: apply
      credential-mode: docker-config
      docker-config-path: ${{ runner.temp }}/nuget-auth/NuGet.Config
      op-map-file: ${{ steps.nuget-auth-prepare.outputs.op-map-file }}

  - name: Build image
    uses: docker/build-push-action@v7
    with:
      context: ${{ inputs.context }}
      file: ${{ inputs.dockerfile }}
      push: false
      secret-files: |
        nuget_config=${{ steps.nuget-auth.outputs.docker-config-path }}
```

Example Dockerfile restore line:

```dockerfile
# syntax=docker/dockerfile:1
RUN --mount=type=secret,id=nuget_config,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet restore src/MyProduct/MyProduct.csproj --locked-mode
```

The Dockerfile must not `COPY` a credentialed `NuGet.Config` into the image.

The workflow must not pass NuGet credentials through `build-args`.

## Security Rules

Do not run private-feed credentialed workflows for untrusted fork pull requests.

If a fork PR requires private packages, use a trusted branch flow, a dependency mirror that needs no secret, or a separate maintainer-triggered trusted workflow.

Do not use `pull_request_target` to run fork code with these credentials.

Do not write the raw JSON secret to logs, summaries, outputs, artifacts, cache keys, Docker build args, Docker labels, Docker image metadata, or package metadata.

Mask every password value and each generated `NuGetPackageSourceCredentials_*` value before exporting it.

Keep `NUGET_PACKAGES` in the workspace cache as today, but never cache `NuGet.Config`, `GITHUB_ENV`, `RUNNER_TEMP`, or generated auth files.

Delete generated Docker secret files with an `if: always()` cleanup step.

Summaries may report only `NuGet auth configured: true` and count of configured sources.

Summaries must not print source URLs if those are treated as sensitive by a caller.

## Files To Create Or Modify

Create `.github/actions/setup-nuget-auth/action.yml`.

Create `.github/actions/setup-nuget-auth/configure-nuget-auth.cs`.

Create `.github/actions/setup-nuget-auth/AGENTS.md`.

Modify `.github/actions/setup-dotnet/action.yml`.

Modify `.github/actions/setup-dotnet/AGENTS.md`.

Modify `.github/actions/dotnet-pack-nuget/action.yml`.

Modify `.github/workflows/wf-dotnet-format.yml`.

Modify `.github/workflows/wf-dotnet-test.yml`.

Modify `.github/workflows/wf-setup-dotnet-generated-code.yml`.

Modify `.github/workflows/wf-verify-publish-nuget.yml`.

Modify `.github/workflows/wf-publish-nuget.yml`.

Modify `.github/workflows/wf-verify-publish-container-dotnet.yml`.

Modify `.github/workflows/wf-publish-container-dotnet.yml`.

Modify `.github/workflows/wf-verify-deploy-k8s-aspire.yml`.

Modify `.github/workflows/wf-deploy-k8s-aspire.yml`.

Modify all matching schemas under `schemas/workflow-inputs/*.schema.json` when new public inputs are added.

Modify `docs/workflow-catalog.md` by regenerating schema-backed tables and adding credential guidance sections.

Modify `README.md`, `docs/security-model.md`, `docs/caching.md`, and `docs/references.md`.

Modify `scripts/validate-workflows.cs`.

Modify or add workflow fixtures under `tests/fixtures/workflow-contract/`.

Modify or add action fixtures under `tests/fixtures/action-contract/`.

## Implementation Tasks

### Task 1: Document the credential decision

**Files:**

- Create: `docs/adr/0006-private-nuget-restore-credentials.md`
- Modify: `docs/references.md`

- [ ] Write an ADR that records the selected credential model.

The ADR should state that host restore uses NuGet environment variable credentials and Docker restore uses BuildKit secret files.

The ADR should state that `NUGET_AUTH_JSON` supports literal values, `op://` values, `github://actor`, and `github://token`.

The ADR should state that `op://` values are resolved by `1password/load-secrets-action@v4` with a CI-provided `OP_SERVICE_ACCOUNT_TOKEN`.

The ADR should state that workflows and scripts must not invoke the `op` CLI directly.

The ADR should reject `actions/setup-dotnet` `source-url` as the only platform mechanism because it is single-source oriented.

The ADR should reject `secrets: inherit` as the default because it passes more secret material than needed.

- [ ] Add the official docs and upstream issue links listed in this plan to `docs/references.md`.

- [ ] Commit with message `docs: record private nuget credential strategy`.

### Task 2: Add the NuGet auth composite action

**Files:**

- Create: `.github/actions/setup-nuget-auth/action.yml`
- Create: `.github/actions/setup-nuget-auth/configure-nuget-auth.cs`
- Create: `.github/actions/setup-nuget-auth/AGENTS.md`
- Test: `tests/fixtures/action-contract/setup-nuget-auth-local.yml`

- [ ] Create `setup-nuget-auth` as a composite action with inputs `nuget-auth-json`, `phase`, `credential-mode`, `op-env-file`, `op-map-file`, `docker-config-path`, and `configured-source-names`.

The `phase` input should allow `prepare`, `apply`, and `cleanup`.

The `credential-mode` input should allow `env`, `docker-config`, and `both`.

The default `credential-mode` should be `env`.

- [ ] Implement `configure-nuget-auth.cs` as a .NET file-based script.

The script should parse the JSON into a strongly typed model.

The script should validate `version == 1`.

The script should validate that `phase=prepare` is safe to run before 1Password values exist.

The script should validate that `phase=apply` fails when unresolved `op://` values are present and the expected generated environment variables are missing.

The script should validate that `github://token` fails when `GITHUB_TOKEN_FOR_NUGET_AUTH` is missing.

The script should reject duplicate source names.

The script should reject source names that cannot be represented as `NuGetPackageSourceCredentials_{name}` environment variables.

The script should require `username` and `password` for every source.

The script should require `source` when `credential-mode` includes `docker-config`.

The script should resolve `github://actor` from `GITHUB_ACTOR`.

The script should resolve `github://token` from `GITHUB_TOKEN_FOR_NUGET_AUTH`.

The script should resolve `op://` values through generated variable names such as `NUGET_AUTH_OP_S1_PASSWORD`.

During `phase=prepare`, the script should write a temporary 1Password env file at `op-env-file` containing only generated variable names and `op://` references.

During `phase=prepare`, the script should write `op-map-file` with the mapping from source fields to generated variable names.

During `phase=prepare`, the script should output `op-required=true` only when at least one field starts with `op://`.

During `phase=prepare`, the script should create parent directories under `RUNNER_TEMP` and reject paths outside `RUNNER_TEMP`.

The script should write `::add-mask::` commands for each password and full credential string.

The script should append `NuGetPackageSourceCredentials_{name}=Username=...;Password=...;ValidAuthenticationTypes=...` lines to `$GITHUB_ENV` when `phase=apply` and `credential-mode` includes `env`.

The script should write a generated `NuGet.Config` to `docker-config-path` when `phase=apply` and `credential-mode` includes `docker-config`.

The generated Docker config should include `<clear />`, `<packageSources>`, and `<packageSourceCredentials>`.

The generated Docker config should use `ClearTextPassword` only in the temporary BuildKit secret file.

During `phase=cleanup`, the script should delete `op-env-file`, `op-map-file`, and `docker-config-path` when present.

During `phase=cleanup`, the script should append empty values for each `NuGetPackageSourceCredentials_{name}` listed in `configured-source-names` to `$GITHUB_ENV`.

The script should write only non-sensitive outputs such as `configured=true`, `op-required=true`, `source-count=2`, `source-names=github,internal`, `op-env-file=<path>`, `op-map-file=<path>`, and `docker-config-path=<path>`.

- [ ] Add an action fixture that passes a fake literal `NUGET_AUTH_JSON` and asserts that expected non-secret outputs exist.

- [ ] Add an action fixture that uses `github://actor` and `github://token` with fake environment values.

- [ ] Add an action fixture that runs `phase=prepare` for fake `op://` references and asserts that only generated variable names and `op://` references appear in the env file.

- [ ] Add an action fixture that runs `phase=apply` with pre-populated fake `NUGET_AUTH_OP_*` values instead of calling 1Password.

- [ ] Commit with message `feat: add private nuget auth setup action`.

### Task 3: Wire host-side restore workflows

**Files:**

- Modify: `.github/actions/setup-dotnet/action.yml`
- Modify: `.github/actions/setup-dotnet/AGENTS.md`
- Modify: `.github/actions/dotnet-pack-nuget/action.yml`
- Modify: `.github/workflows/wf-dotnet-format.yml`
- Modify: `.github/workflows/wf-dotnet-test.yml`
- Modify: `.github/workflows/wf-setup-dotnet-generated-code.yml`
- Modify: `.github/workflows/wf-verify-publish-nuget.yml`
- Modify: `.github/workflows/wf-publish-nuget.yml`
- Modify: `.github/workflows/wf-verify-deploy-k8s-aspire.yml`
- Modify: `.github/workflows/wf-deploy-k8s-aspire.yml`

- [ ] Add optional `NUGET_AUTH_JSON` under `on.workflow_call.secrets` for each affected reusable workflow.

- [ ] Add optional `OP_SERVICE_ACCOUNT_TOKEN` under `on.workflow_call.secrets` for each affected reusable workflow.

- [ ] In `.github/actions/setup-dotnet/action.yml`, add an optional `nuget-auth-json` input.

- [ ] In `.github/actions/setup-dotnet/action.yml`, add an optional `op-service-account-token` input.

- [ ] In `.github/actions/setup-dotnet/action.yml`, call `setup-nuget-auth` with `phase=prepare` after `actions/setup-dotnet@v5` and before cache, tool restore, or dependency restore.

- [ ] In `.github/actions/setup-dotnet/action.yml`, call `1password/load-secrets-action@v4` when the prepare step outputs `op-required=true`.

- [ ] In `.github/actions/setup-dotnet/action.yml`, call `setup-nuget-auth` with `phase=apply` and `credential-mode=env` after 1Password loading and before any restore.

- [ ] In `.github/actions/setup-dotnet/action.yml`, call `setup-nuget-auth` with `phase=cleanup` in an `if: always()` cleanup step.

- [ ] In `.github/actions/dotnet-pack-nuget/action.yml`, add an optional `nuget-auth-json` input and call `setup-nuget-auth` before `Restore package project`.

- [ ] In `.github/actions/dotnet-pack-nuget/action.yml`, add an optional `op-service-account-token` input and use the same prepare, optional 1Password load, apply, and cleanup sequence.

- [ ] Pass `${{ secrets.NUGET_AUTH_JSON }}` from workflows into the relevant composite action inputs.

- [ ] Pass `${{ secrets.OP_SERVICE_ACCOUNT_TOKEN }}` from workflows into the relevant composite action inputs.

- [ ] Pass `GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}` to the prepare and apply steps.

- [ ] Derive non-secret presence flags such as `NUGET_AUTH_JSON_PRESENT` and `OP_SERVICE_ACCOUNT_TOKEN_PRESENT` through job `env` before using them in `if:` conditionals.

- [ ] For inline workflows, check out `.github/actions/setup-nuget-auth` from the platform repository before invoking it.

- [ ] Update summaries to include only a boolean and source count.

- [ ] Commit with message `feat: support private nuget restore credentials`.

### Task 4: Wire Docker Buildx workflows

**Files:**

- Modify: `.github/workflows/wf-verify-publish-container-dotnet.yml`
- Modify: `.github/workflows/wf-publish-container-dotnet.yml`
- Test: `tests/fixtures/workflow-contract/dotnet-container-verify-local.yml`
- Test: `tests/fixtures/workflow-contract/container-publish-consumer.yml`

- [ ] Add optional secret `NUGET_AUTH_JSON`.

- [ ] Add optional secret `OP_SERVICE_ACCOUNT_TOKEN`.

- [ ] Add public input `nuget-build-secret` with type `boolean` and default `false`.

- [ ] Checkout `.github/actions/setup-nuget-auth` when `nuget-build-secret` is true.

- [ ] Run `setup-nuget-auth` with `phase=prepare` when `nuget-build-secret` is true.

- [ ] Run `1password/load-secrets-action@v4` when the prepare step outputs `op-required=true`.

- [ ] Generate `${RUNNER_TEMP}/nuget/NuGet.Config` through `setup-nuget-auth` with `phase=apply` and `credential-mode=docker-config`.

- [ ] Pass the generated file to `docker/build-push-action@v7` with `secret-files: nuget_config=${{ steps.nuget-auth.outputs.docker-config-path }}`.

- [ ] Fail fast when `nuget-build-secret` is true and `NUGET_AUTH_JSON` is empty.

- [ ] Fail fast when `nuget-build-secret` is true, `NUGET_AUTH_JSON` contains `op://`, and `OP_SERVICE_ACCOUNT_TOKEN` is empty.

- [ ] Pass `GITHUB_TOKEN_FOR_NUGET_AUTH: ${{ github.token }}` to the prepare and apply steps.

- [ ] Derive non-secret presence flags such as `NUGET_AUTH_JSON_PRESENT` and `OP_SERVICE_ACCOUNT_TOKEN_PRESENT` through job `env` before using them in `if:` conditionals.

- [ ] Add an `if: always()` cleanup step that calls `setup-nuget-auth` with `phase=cleanup`.

- [ ] Document the required Dockerfile `RUN --mount=type=secret,id=nuget_config,target=/root/.nuget/NuGet/NuGet.Config` shape.

- [ ] Commit with message `feat: pass nuget auth to dotnet container builds`.

### Task 5: Update schemas and generated docs

**Files:**

- Modify: `schemas/workflow-inputs/wf-verify-publish-container-dotnet.schema.json`
- Modify: `schemas/workflow-inputs/wf-publish-container-dotnet.schema.json`
- Modify: `docs/workflow-catalog.md`
- Modify: `README.md`
- Modify: `docs/security-model.md`
- Modify: `docs/caching.md`

- [ ] Add `nuget-build-secret` to the two container workflow schemas.

- [ ] Run `rtk dotnet run --file scripts/generate-docs.cs`.

- [ ] Add a hand-written workflow catalog section explaining `NUGET_AUTH_JSON`, committed `NuGet.Config`, package source mapping, 1Password `op://` references, GitHub runtime references, and Docker secret mounts.

- [ ] Update `README.md` with a short literal-token example for one private feed.

- [ ] Update `README.md` with a multiple-feed example that mixes `op://`, `github://actor`, and `github://token`.

- [ ] Update `docs/security-model.md` with the trusted-build restriction for private feed credentials.

- [ ] Update `docs/security-model.md` to state that the 1Password service account token is only available to trusted workflows and is never passed to Docker builds.

- [ ] Update `docs/caching.md` to explicitly forbid caching generated NuGet auth files, `GITHUB_ENV`, and `RUNNER_TEMP`.

- [ ] Commit with message `docs: document private nuget restore credentials`.

### Task 6: Strengthen validation

**Files:**

- Modify: `scripts/validate-workflows.cs`
- Modify: `tests/validate-workflow-input-schema-parity.ps1`

- [ ] Add `ValidatePrivateNuGetCredentialContract()`.

- [ ] Require every public workflow that runs .NET restore or tool restore to expose optional `NUGET_AUTH_JSON`.

- [ ] Require container workflows with `nuget-build-secret` to use `secret-files`, not `build-args`.

- [ ] Fail validation if workflow text contains `NUGET_AUTH_JSON` in summaries, artifact manifests, cache keys, Docker labels, or Docker build args.

- [ ] Fail validation if workflow text invokes the `op` CLI directly.

- [ ] Fail validation if workflow text references `secrets.NUGET_AUTH_JSON` or `secrets.OP_SERVICE_ACCOUNT_TOKEN` directly in `if:` conditionals.

- [ ] Fail validation if a workflow using `op://` support does not use `1password/load-secrets-action@v4`.

- [ ] Fail validation if 1Password env files are not under `${{ runner.temp }}` or `$RUNNER_TEMP`.

- [ ] Fail validation if `OP_SERVICE_ACCOUNT_TOKEN`, `NuGetPackageSourceCredentials_`, or generated `NUGET_AUTH_OP_` values appear in summaries, artifact manifests, cache keys, Docker labels, or Docker build args.

- [ ] Fail validation if generated Docker NuGet auth files are uploaded as artifacts.

- [ ] Keep schema parity validation passing for all public workflow inputs.

- [ ] Commit with message `test: validate private nuget credential contract`.

### Task 7: Verify end to end

**Files:**

- Modify: `tests/fixtures/action-contract/setup-nuget-auth-local.yml`
- Modify: `tests/fixtures/workflow-contract/dotnet-nuget-library-local.yml`
- Modify: `tests/fixtures/workflow-contract/dotnet-nuget-verify-local.yml`
- Modify: `tests/fixtures/workflow-contract/dotnet-generated-code-consumer.yml`

- [ ] Add fixtures for a literal private feed credential.

- [ ] Add fixtures for `github://actor` and `github://token` resolution.

- [ ] Add fixtures for `op://` prepare and apply with pre-populated fake `NUGET_AUTH_OP_*` environment variables.

- [ ] Run `rtk dotnet run --file scripts/generate-docs.cs -- --check`.

- [ ] Run `rtk dotnet run --file scripts/validate-workflows.cs`.

- [ ] Run `rtk powershell -NoProfile -File tests/validate-workflow-input-schema-parity.ps1`.

- [ ] Run `rtk actionlint` if actionlint is installed.

- [ ] Run bounded `act` smoke tests for one host restore fixture and one container fixture when local Docker is available.

- [ ] Inspect produced artifacts and summaries to confirm that no fake token value appears.

- [ ] Commit with message `test: cover private nuget credential flows`.

## Self-Review

Spec coverage is complete for the user questions.

The plan explains what is necessary, confirms that multiple credentials can be provided, and shows caller workflow, JSON secret, committed `NuGet.Config`, and Dockerfile examples.

The plan avoids assuming private feed vendor details beyond standard NuGet username and token credentials.

The plan keeps untrusted fork PRs out of secret-bearing jobs.

The plan avoids putting secret values into workflow inputs, logs, outputs, artifacts, caches, Docker build args, or images.

The main consequential decision is whether to encode multiple credentials in one JSON secret or use a fixed set of named workflow secrets.

This plan recommends one JSON secret because reusable workflow secret names are static and because the approach supports any number of feeds without widening the public workflow API each time.
