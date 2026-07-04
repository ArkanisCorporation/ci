# node-docs/AGENTS.md

## Scope

Use these instructions for Node.js apps, docs sites, and package-manager compatibility jobs.
Known examples are CitizenId-docs and CitizenId-medusa-store.

## Goal

Move install, lint, test, and build into split Node workflows.
Keep Pages deployment, release creation, service orchestration, and package-manager compatibility matrices explicit.

## Inspection Order

1. Read all Node workflows, package.json, lockfiles, and packageManager field.
2. Identify npm, pnpm, and yarn coverage requirements.
3. Identify deploy steps such as GitHub Pages, release creation, service startup, Redis, Postgres, or CLI smoke tests.

## Target Shape

- Use `wf-setup-node.yml` for standard install.
- Use `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` for expensive script lanes.
- Use `package-manager-version` or package.json `packageManager` for pnpm and yarn.
- Keep Pages deployment in a separate deploy job.
- Keep service integration tests in repo-specific jobs that depend on setup verification.
- Keep package-manager matrix jobs only when compatibility is a product requirement.

## Rules

- Do not remove lockfile enforcement.
- Do not mutate lockfiles in CI to make cache work.
- Do not use old checkout/setup-node action majors.
- Do not run dependency lifecycle scripts on untrusted pull requests unless the repository accepts that risk.
- Do not hide service startup and teardown inside setup workflow inputs.

## Verification

- Run `wf-setup-node.yml` with `enable-cache: false` once for cold-install confidence.
- Run lint, test, and build workflows independently when the repository has those scripts.
- Run app-specific service tests separately.
- Verify Pages or release deploy jobs require appropriate permissions and protected refs.
- Verify package-manager matrix jobs use explicit versions.
