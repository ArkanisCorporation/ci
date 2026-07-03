# .github/workflows/AGENTS.md

## Scope

Applies to public reusable workflows in `.github/workflows/*.yml`.

## Hard Rules

- Workflow files must live directly under `.github/workflows`.
- Public workflow files use `wf-*.yml` names and include `on.workflow_call`.
- Declare every input, secret, output, permission, artifact, precondition, and side effect.
- Keep top-level `permissions: {}`.
- Grant only job-level permissions that the job needs.
- External actions should use stable version tags unless a specific incident requires a SHA pin.
- Do not use dynamic `uses` targets.
- Avoid nested reusable workflows unless the benefit is clear.
- Avoid `pull_request_target`.
- Treat caller inputs and GitHub event text as untrusted.
- When a reusable workflow checks out this platform repository for a local composite action, resolve the called workflow source from `fromJSON(toJSON(job)).workflow_repository` and `fromJSON(toJSON(job)).workflow_sha`.
- `fromJSON(toJSON(job))` is only there because our pinned actionlint:1.7.12 does not know the newer documented job.workflow_ref / job.workflow_sha fields.
- Do not use `github.workflow_ref` or `github.workflow_sha` for platform action source checkout because the `github` context is caller-scoped in reusable workflows.

## Runner Model

- Every public workflow accepts `runs-on-json`.
- Every public workflow accepts `runs-on-self-hosted`.
- `runs-on-json` selects GitHub-hosted images or self-hosted labels.
- `runs-on-self-hosted` selects behavior gates for hosted or self-hosted assumptions.
- Self-hosted jobs must record runner OS, arch, disk, workspace, and required tool availability.
- Hosted-only steps must use `if: ${{ !inputs.runs-on-self-hosted }}`.
- Self-hosted-only steps must use `if: inputs.runs-on-self-hosted`.
- Remote BuildKit, cluster kube contexts, Docker sockets, and preinstalled tools are self-hosted capabilities that must be explicit.

## Workflow Shape

Recommended order:

1. Checkout caller repository.
2. Setup required toolchain.
3. Validate runner contract and inputs.
4. Run self-hosted preflight when applicable.
5. Restore dependencies.
6. Build, test, package, publish, or deploy.
7. Write metadata and artifact manifest.
8. Write step summary.
9. Upload diagnostics with `if: always()`.

## Current Public Workflows

- `wf-setup-dotnet.yml` verifies .NET restore, format, build, tests, coverage, and diagnostics.
- `wf-setup-dotnet-generated-code.yml` verifies generated .NET source and fails when codegen commands create a Git diff.
- `wf-setup-dotnet-jetbrains.yml` verifies JetBrains ReSharper CleanupCode and fails when cleanup creates a Git diff.
- `wf-setup-node.yml` verifies Node.js install, lint, tests, build, cache, and diagnostics.
- `wf-lint-github-actions.yml` lints caller GitHub Actions workflows with actionlint.
- `wf-verify-release-semantic.yml` verifies semantic-release without publishing.
- `wf-release-semantic.yml` runs semantic-release metadata without `@semantic-release/exec`.
- `wf-release-backpropagation.yml` creates release branch backpropagation pull requests and can auto-merge them.
- `wf-verify-publish-nuget.yml` packs NuGet packages without publishing.
- `wf-publish-nuget.yml` packs and publishes NuGet packages through Trusted Publishing or API-key fallback.
- `wf-verify-publish-container-dotnet.yml` stamps .NET versions and builds OCI images without pushing.
- `wf-publish-container-dotnet.yml` stamps .NET versions before publishing OCI images through Docker Buildx.
- `wf-verify-deploy-k8s-aspire.yml` verifies Aspire Kubernetes deployment inputs without applying cluster changes.
- `wf-deploy-k8s-aspire.yml` deploys an Aspire AppHost to Kubernetes.
- `wf-platform-selftest.yml` validates this platform repository.

## Repository Workflows

- `verify-release.yml` runs platform selftest before semantic-release verification on pull requests and manual dispatch, with production semantic-release plugins installed for config parity.
- `release.yml` runs platform selftest before environment-gated semantic-release publication and mutable major tag updates on main pushes and manual dispatch.

## Security Rules

- `pull_request` may build and test without secrets.
- Publish and deploy workflows require protected refs or environments in the caller.
- Trusted Publishing requires `id-token: write`.
- Docker and NuGet publish jobs must fail closed when expected artifacts are missing.
- K8s deployment consumes explicit environment, namespace, AppHost path, and optional image tag.
- Do not deploy mutable image tags to production unless a documented exception exists.

## Observability Rules

- Every workflow writes `$GITHUB_STEP_SUMMARY`.
- Every workflow writes `run-metadata.json` where practical.
- Every workflow writes `artifact-manifest.json` when it produces artifacts.
- Upload diagnostic artifacts even on failure.
- Summaries include runner kind and requested runner labels.

## Review Questions

- Can a consumer call this with documented inputs only?
- Can a consumer override the hosted image or self-hosted labels?
- Are hosted and self-hosted assumptions gated?
- Can a fork PR exfiltrate a secret?
- Can failure be diagnosed from summary plus artifacts?
- Can release, publish, or deploy rerun safely?
