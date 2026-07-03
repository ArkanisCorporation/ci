# setup-node/AGENTS.md

## Scope

Node/TypeScript package-manager setup.

## Rules

- Support npm, pnpm, yarn; require lockfile for CI install.
- Use package-manager cache keyed by lockfile + Node version + OS/arch.
- Prefer `npm ci`, `pnpm install --frozen-lockfile`, `yarn install --immutable`.
- Print Node + package-manager versions.
- Do not run lifecycle scripts from untrusted PR unless workflow explicitly permits risk.
- Keep action catalog Node24-compatible.
