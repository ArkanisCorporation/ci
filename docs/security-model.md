# Security Model

Audience: platform maintainers and security reviewers.

## Baseline

Default-deny token permissions are mandatory.
Every workflow declares `permissions: {}` at the top level.
Jobs add only the scopes they need.

External action references use stable version tags by default.
SHA pins remain available for incident response or high-risk exceptions.

## Trust Zones

| Zone | Rule |
|---|---|
| untrusted | Build and test only. |
| trusted-build | Build packages, images, metadata, and attestations. |
| publish | Publish packages, images, releases, or tags. |
| deploy | Change external environments. |

Artifacts moving into a higher-trust zone require a manifest and digest or provenance decision.

## Event Rules

| Event | Rule |
|---|---|
| `pull_request` | Safe for build and test without secrets. |
| `pull_request_target` | Not allowed in platform workflows. |
| `workflow_run` | Treat upstream artifacts as untrusted until verified. |
| `push` | Trusted only under caller branch protection and actor policy. |
| `workflow_dispatch` | Publish and deploy require protected environments. |
| `release` | Publish only after artifact verification. |

## Semantic Release

Pull requests in `release.yml` run semantic-release in dry-run mode with `contents: write` so semantic-release can verify tag push authorization.
`wf-release-semantic.yml` publishes release metadata from an environment-gated job.
This platform repository also updates mutable major version tags such as `v1` through `semantic-release-major-tag` in the production release workflow.
The repository release workflow installs the same plugin for dry-runs so verification loads the production semantic-release configuration.
It does not run verification or publishing scripts.
It rejects `@semantic-release/exec` by default.
Use standalone jobs for pre-release verification, package publishing, image publishing, and deployment.

This separation keeps release metadata from becoming a privileged shell-script dispatcher.

## NuGet Trusted Publishing

Trusted Publishing is preferred over long-lived API keys.
NuGet currently validates the workflow that requests the OIDC token.
For caller-owned nuget.org policies, keep that token request in the consumer repository workflow.
Use `.github/actions/dotnet-pack-nuget`, then `NuGet/login`, then `.github/actions/dotnet-publish-nuget` with `api-key: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}` in the same protected job.
`wf-publish-nuget.yml` still uses `NuGet/login` when `trusted-publishing` is true, so use it only when the nuget.org policy intentionally matches that reusable workflow.
The NuGet user resolves from `nuget-user`, then a caller `NUGET_USER` secret, then a caller repository, organization, or environment configuration variable named `NUGET_USER`.
Prefer the input or configuration variable because NuGet profile and organization names are normally not secret.
Use the secret only when the caller deliberately treats the NuGet owner as sensitive.
Caller workflow `env` values do not cross the reusable workflow boundary.
Named secrets must be passed with `jobs.<job_id>.secrets`, unless trusted same-organization callers intentionally use `secrets: inherit`.
If the called publish job binds an environment that also has a `NUGET_USER` secret, GitHub environment-secret precedence can shadow a caller-passed secret with the same name.
Only the Trusted Publishing job needs `id-token: write`.
The API-key fallback job uses `contents: read` plus the explicit `NUGET_API_KEY` secret.

Important edge case:
NuGet Trusted Publishing validates the workflow that requests the OIDC token.
Consumers must configure the nuget.org policy to match the reusable workflow behavior they use.
If that does not fit a package policy, use the composite action pattern or API-key fallback.

Caller-owned Trusted Publishing:
Run `NuGet/login` and `dotnet-publish-nuget` in the same protected caller job when the nuget.org policy must match the caller workflow file exactly.
The `NuGet/login` output is a short-lived API key for subsequent steps in that job.
Do not pass that value through job outputs or artifacts.
If reusable platform packaging is desired, call `dotnet-pack-nuget` earlier in the same job before requesting the short-lived key.

## Private NuGet Restore Credentials

Private restore credentials are allowed only when the caller explicitly passes `NUGET_AUTH_JSON`.
Workflows also accept `OP_SERVICE_ACCOUNT_TOKEN` when any `NUGET_AUTH_JSON` value starts with `op://`.
The 1Password service account token is only for trusted workflows and is never passed to Docker Buildx.
The workflow resolves `op://` values through `1password/load-secrets-action@v4`.
The workflow must not invoke the 1Password `op` CLI directly.
The workflow resolves `github://actor` from `github.actor`.
The workflow resolves `github://token` from `github.token`, passed into auth setup as `GITHUB_TOKEN_FOR_NUGET_AUTH`.
For host restore, credentials are written as masked `NuGetPackageSourceCredentials_{name}` environment variables.
For Dockerfile restore, credentials are written to a temporary `NuGet.Config` under `RUNNER_TEMP` and mounted with Docker BuildKit `secret-files`.
Do not run private-feed credentialed jobs on untrusted fork pull requests.
Do not use `pull_request_target` to run fork code with private feed credentials.
Do not write `NUGET_AUTH_JSON`, `OP_SERVICE_ACCOUNT_TOKEN`, `NuGetPackageSourceCredentials_*`, `NUGET_AUTH_OP_*`, generated 1Password env files, or generated Docker NuGet configs to logs, summaries, outputs, artifacts, cache keys, Docker labels, Docker build args, or image metadata.

## Secrets

Do not print secrets.
Do not transform secrets and print the transformed value.
Do not pass secrets through outputs.
Do not put secrets in Docker build args.
Use OIDC or secret mounts when possible.

GitHub does not allow `secrets` directly in `if:` expressions.
Map secret presence to job environment values when a conditional step needs to check whether a secret was supplied.

## Runners

Hosted and self-hosted paths must be explicit through `runs-on-self-hosted`.
Self-hosted workflows must preflight tool availability and disk state.
Self-hosted labels define capabilities such as Docker, remote BuildKit, Kubernetes, and SDK support.

## Token Rules

| Scope | Use |
|---|---|
| `contents: read` | Checkout and read repository source. |
| `contents: write` | Release tags, changelog commits, or GitHub releases. |
| `issues: write` | semantic-release issue comments. |
| `pull-requests: write` | semantic-release PR comments or release backpropagation. |
| `packages: write` | GHCR or package registry push. |
| `id-token: write` | OIDC for Trusted Publishing, cloud auth, or provenance. |
| `attestations: write` | Artifact or image attestations. |
