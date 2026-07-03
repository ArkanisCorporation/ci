# AGENTS.md

## Scope

Applies repo-wide.
Nested `AGENTS.md` files add stricter subtree rules.
Closest file wins unless it weakens security.

## Mission

Build a shared GitHub Actions platform for Arkanis projects.
Reusable workflows in `.github/workflows/*.yml` are the public API.
Composite actions in `.github/actions/**` are reusable step bundles, but external reusable workflows must not assume relative local actions resolve to this platform repository.
Docs, schemas, policies, fixtures, and validation scripts are part of the compatibility contract.

## Non-Negotiables

- Check current GitHub docs before changing platform behavior, security semantics, runner behavior, cache behavior, artifact behavior, or reusable workflow contracts.
- Prefer stable version tags for external actions and workflow examples.
- Use `permissions: {}` at workflow top level and minimal job permissions.
- Never checkout or run fork code in `pull_request_target`.
- Never put secrets in logs, outputs, artifacts, caches, Docker build args, or summary text.
- Prefer OIDC and short-lived credentials over long-lived secrets.
- Keep verification, release metadata, package publishing, image publishing, and deployment as separate jobs or workflows.
- Avoid `@semantic-release/exec` for verification and publishing.
- Every workflow change updates docs, schemas, fixtures, and validation.

## Public Workflow Contract

- Public workflow files use `wf-<domain>-<purpose>.yml`.
- Public workflow files include `on.workflow_call`.
- Inputs are explicit, typed, documented, and schema-backed.
- Use `runs-on-json` for runner label or hosted image selection.
- Use `runs-on-self-hosted` so steps can gate self-hosted assumptions.
- Outputs are stable and machine-readable.
- Breaking input, output, permission, artifact, or semantic changes require a major release.

## Runner Contract

- Hosted and self-hosted runners are first-class.
- Consumers choose default runners by passing `runs-on-json`.
- Consumers choose runner behavior by passing `runs-on-self-hosted`.
- Self-hosted jobs must preflight disk, workspace, and required tools.
- Hosted-only assumptions require explicit `!inputs.runs-on-self-hosted` gating.
- Self-hosted-only assumptions require explicit `inputs.runs-on-self-hosted` gating.
- Avoid `sudo`, Docker socket, persistent workspace, or preinstalled tool assumptions unless documented by runner labels.

## Trust Zones

| Zone | Events | Secrets | Token | Work |
|---|---|---|---|---|
| untrusted | `pull_request` from fork | none | read | lint, build, test |
| trusted-build | protected push or internal PR | limited | need-based | package, image, attest |
| publish | release, tag, protected manual | OIDC or package secret | write by need | NuGet, container, release |
| deploy | protected environment | OIDC or kube auth | write by need | deploy digest or chart |

No artifact crosses into a higher-trust zone without a manifest and digest or provenance decision.

## Observability Floor

Each workflow writes a step summary, run metadata, artifact manifest, tool versions, and failure diagnostics.
Use log groups for noisy commands.
Upload diagnostics with `if: always()`.
Summaries must be useful without exposing secrets.

## Development Workflow

1. Read the nearest `AGENTS.md`.
2. Inspect the schema, docs, fixtures, and tests for the target workflow.
3. Verify current upstream docs for platform behavior.
4. Edit the smallest compatible surface.
5. Run validation scripts and actionlint.
6. Run a bounded `act` smoke test when the workflow can run locally.
7. Update docs, schemas, fixtures, and references.

## Review Checklist

- API stable?
- Permissions minimal?
- External actions pinned?
- Fork and PR trust safe?
- Secrets protected from logs, outputs, artifacts, cache, and summaries?
- Self-hosted and hosted runner paths explicit?
- Failure diagnostics useful?
- Cache keys precise?
- Package, image, and deploy outputs manifest-backed?
- Docs, schemas, fixtures, and validation updated?
