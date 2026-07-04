# migrations/AGENTS.md

## Scope

Applies to migration cohort documentation under `docs/migrations/**`.

## Mission

Guide agents that migrate existing Arkanis repositories onto this CI platform.
Keep migration work evidence-driven, reversible, and separated by trust zone.

## Common Rules

- Start by reading the target repository `.github/workflows/**`, `.github/actions/**`, package metadata, and nearest `AGENTS.md`.
- Identify which cohort best matches the repository before editing consumer workflows.
- Prefer reusable workflows from this repository over copied shell or PowerShell scripts.
- Keep verification, release metadata, package publishing, image publishing, and deployment as separate jobs.
- Preserve repository-specific behavior unless a platform workflow already covers it.
- Treat every broad permission, hardcoded runner, unguarded secret, and script-driven release side effect as a migration finding.
- Use `runs-on`, `runs-on-json`, and `runs-on-self-hosted` instead of hardcoded runner labels.
- Use `enable-cache: false` when debugging dependency cache behavior.
- Keep lifecycle-sensitive steps such as signing, Pages deploy, VirusTotal upload, infrastructure apply, and production deploy outside verification workflows.

## Required Output

- State source workflow files reviewed.
- State selected cohort and reason.
- State target workflow shape.
- List secrets, variables, environments, and runner labels required by the migrated workflow.
- List behavior intentionally not migrated.
- Run syntax and platform validation before claiming success.

## Stop Conditions

- Stop if a release job mixes version computation with irreversible publishing and the side effects are unclear.
- Stop if a workflow relies on a secret whose scope or environment protection cannot be determined.
- Stop if self-hosted runner capabilities are assumed but not encoded in labels, docs, or preflight.
- Stop if migration would weaken fork pull request safety.
