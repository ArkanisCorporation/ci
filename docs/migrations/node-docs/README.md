# Node And Docs Migration

Audience: migration agents.

## Evidence

CitizenId-docs uses pnpm, older checkout/setup-node action versions, and a Pages deploy action.
CitizenId-medusa-store has npm, pnpm, and yarn CLI smoke jobs with older setup-node actions and manual lockfile mutation.

## Target

The target is a split Node pipeline.
Lint, test, and build workflows each perform shared setup internally and run independently.
Deploy and release jobs remain separate.
Service-backed CLI checks remain explicit integration jobs.

## Checklist

- Replace lint, test, and build jobs with `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml`.
- Use `package-manager: pnpm`, `npm`, or `yarn`.
- Provide `package-manager-version` when needed.
- Remove manual lockfile creation or deletion from CI.
- Use `enable-cache: false` for one migration smoke run.
- Keep Pages deployment outside verification workflows.
- Keep Redis, Postgres, and dev server smoke tests outside verification workflows.

## Rollback

Keep old deploy jobs unchanged during first verification migration.
Restore old setup steps only if the platform lint, test, or build workflow lacks a documented package-manager behavior.
