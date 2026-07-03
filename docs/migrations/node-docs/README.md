# Node And Docs Migration

Audience: migration agents.

## Evidence

CitizenId-docs uses pnpm, older checkout/setup-node action versions, and a Pages deploy action.
CitizenId-medusa-store has npm, pnpm, and yarn CLI smoke jobs with older setup-node actions and manual lockfile mutation.

## Target

The target is a split Node pipeline.
Setup workflow handles install, lint, test, build, cache, metadata, and diagnostics.
Deploy and release jobs remain separate.
Service-backed CLI checks remain explicit integration jobs.

## Checklist

- Replace setup/install/lint/test/build with `wf-setup-node.yml`.
- Use `package-manager: pnpm`, `npm`, or `yarn`.
- Provide `package-manager-version` when needed.
- Remove manual lockfile creation or deletion from CI.
- Use `enable-cache: false` for one migration smoke run.
- Keep Pages deployment outside setup workflow.
- Keep Redis, Postgres, and dev server smoke tests outside setup workflow.

## Rollback

Keep old deploy jobs unchanged during first setup migration.
Restore old setup steps only if `wf-setup-node.yml` lacks a documented package-manager behavior.
