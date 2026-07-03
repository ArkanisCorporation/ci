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

The release verification workflow runs semantic-release in dry-run mode with `contents: read`.
`wf-release-semantic.yml` publishes release metadata from an environment-gated job.
It does not run verification or publishing scripts.
It rejects `@semantic-release/exec` by default.
Use standalone jobs for pre-release verification, package publishing, image publishing, and deployment.

This separation keeps release metadata from becoming a privileged shell-script dispatcher.

## NuGet Trusted Publishing

Trusted Publishing is preferred over long-lived API keys.
`wf-publish-nuget.yml` uses `NuGet/login` when `trusted-publishing` is true.
Only the Trusted Publishing job needs `id-token: write`.
The API-key fallback job uses `contents: read` plus the explicit `NUGET_API_KEY` secret.

Important edge case:
NuGet Trusted Publishing validates the workflow that requests the OIDC token.
Consumers must configure the nuget.org policy to match the reusable workflow behavior they use.
If that does not fit a package policy, use API-key fallback or keep the `NuGet/login` step in the consumer repository.

Caller-owned Trusted Publishing:
Run `NuGet/login` and `dotnet nuget push` in the same protected caller job when the nuget.org policy must match the caller workflow file exactly.
The `NuGet/login` output is a short-lived API key for subsequent steps in that job.
Do not pass that value through job outputs or artifacts.
If reusable platform packaging is still desired, call the package verification workflow first, then download the verified package artifact in the protected caller-owned publish job.

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
