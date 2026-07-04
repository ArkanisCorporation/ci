# ADR-0001: Node Setup Workflow

Status: replaced by ADR-0004

## Context

Arkanis Node.js projects currently repeat setup, cache, install, lint, test, and build steps.
CitizenId-docs uses older checkout/setup-node actions, pnpm/action-setup, setup-node cache, and a Pages deploy action.
CitizenId-medusa-store repeats npm, pnpm, and yarn setup with older action versions.

## Decision

Add `wf-setup-node.yml` as a public reusable workflow.
The workflow supports npm, pnpm, and yarn.
It uses `actions/setup-node@v6` for Node.js.
It uses Corepack for pnpm and yarn package-manager preparation.
It uses `runs-on/cache@v5.0.7` for package-manager store cache when `enable-cache` is true.
It blocks lifecycle scripts in generated install commands unless `allow-lifecycle-scripts` is true.
It exposes `upload-diagnostics` so local `act` smoke tests can skip upload-artifact emulation gaps without changing GitHub-hosted behavior.
It keeps publish, Pages deploy, release creation, and app-specific service tests outside the setup workflow.

## Consequences

Consumers get one reusable verification path for normal Node.js packages.
Consumers with custom service orchestration can call this workflow first, then run app-specific jobs.
pnpm and yarn consumers must provide `package-manager-version` or package.json `packageManager`.
Generated install commands favor fork-PR safety over compatibility with lifecycle-heavy projects.

## Migration

Replace repeated checkout/setup/install jobs with `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` independent verification lanes.
Use `enable-cache: false` when debugging cache behavior.
Use `install-command`, `lint-command`, `test-command`, or `build-command` when a repo needs custom commands.
Keep Pages deployment and release publishing in separate jobs.

## References

- actions/setup-node: https://github.com/actions/setup-node
- Node.js Corepack: https://nodejs.org/api/corepack.html
