# Tasks

## 1. Best-effort uploads

- [x] 1.1 Add `continue-on-error: true` to every diagnostics/metadata `actions/upload-artifact` step and keep the `wf-publish-nuget.yml` package upload required
- [x] 1.2 Add `upload-diagnostics` (boolean, default true) to every workflow with a best-effort upload that lacks it, gate the upload on it, and add the input to each schema
- [x] 1.3 Lower `artifact-retention-days` defaults to 7 for pull-request workflows in workflows and schemas
- [x] 1.4 Extend the validator: best-effort uploads need `continue-on-error: true` and `upload-diagnostics` gating; only allow-listed uploads may be required

## 2. Container-free sqlx job

- [x] 2.1 Rewrite the `wf-rust-check.yml` sqlx job: no `container:`/`services:`, Docker preflight, `docker run` Postgres with run-scoped name and random port, bounded TCP wait, bridge gateway under `/.dockerenv`, `always()` cleanup
- [x] 2.2 Deprecate `sqlx-container-image` (schema, description, warning on non-default value)
- [x] 2.3 Extend the validator: the sqlx job must not use `container:` or `services:` and must clean up its Postgres container
- [x] 2.4 Add a fixture migration to the Rust mock workspace and enable `sqlx-prepare-check` in `release.yml`'s `rust-check` job

## 3. Docs

- [x] 3.1 Update `docs/artifacts.md` (best-effort vs required, retention), the catalog Rust section and diagram, `docs/runner-contract.md`, `docs/caching.md`, ADR-0009, `AGENTS.md` observability floor, and mock-projects README
- [x] 3.2 Regenerate catalog input tables (`tests/snapshots/docs` holds no doc copies, nothing to refresh)

## 4. Verification

- [x] 4.1 Run `scripts/validate-workflows.cs`, `scripts/generate-docs.cs --check`, `actionlint`, and `openspec validate best-effort-artifact-uploads --strict`
- [ ] 4.2 Prove the sqlx job and best-effort uploads in the pull request run on `daedalus`

## Workflow follow-up

- Archive `add-rust-workflows` before this change: the `rust-verification` delta here is a MODIFIED
  requirement and needs that spec to exist in `openspec/specs/`.
- After release, MentionBot drops its local `sqlx` job and uses `sqlx-prepare-check` (ARK-693 follow-up).
