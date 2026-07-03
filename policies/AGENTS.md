# policies/AGENTS.md

## Scope

Applies machine-readable policy files under `policies/**`.

## Rules

- Policies are enforcement inputs, not prose.
- Prefer allowlists over denylists.
- Include rationale comments only when parser-safe.
- Tighten in minor; loosen only with ADR/security review.
- Keep policy names stable; scripts/tests depend on them.
- Every policy change updates docs + tests.

## Categories

- Allowed actions.
- Permissions baseline.
- Artifact retention.
- Runner labels.
- Release channels.
- Cache policy.
- Environment/deploy gates.
