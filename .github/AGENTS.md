# .github/AGENTS.md

## Scope

Applies to `.github/**`: workflows, actions, issue/PR templates, repo automation.

## Rules

- Do not weaken repo security defaults.
- Keep platform CI self-testing before release.
- Avoid repo-specific assumptions; this repo serves many consumers.
- For GitHub app/token work, document exact permission needed.
- Use `CODEOWNERS` for public workflows/actions once teams exist.
- Add `SECURITY.md` before external adoption.

## Workflow/action catalog

Maintain single catalog in docs:

- Workflow name.
- Purpose.
- Inputs/secrets/outputs.
- Required permissions.
- Artifact contract.
- Runner labels.
- Trust zone.
- Breaking-change notes.

## Release refs

- `main` = dogfood/dev.
- `vN` = stable major ref.
- Consumers use stable version tags from the release catalog.
- Do not recommend branch refs in production examples.

## Checks

Every PR touching `.github/**` must validate:

- YAML parses.
- `workflow_call` input schema matches docs.
- External `uses:` entries pinned or explicitly test-only.
- `permissions` present and minimal.
- No disallowed trigger/secrets pattern.
- Artifact/summary/diagnostics still emitted.
