# Tasks

## 1. Rust mock project

- [x] 1.1 Add `tests/fixtures/mock-projects/rust-workspace` (one library crate plus binary, `rust-toolchain.toml`, `deny.toml`, `.config/nextest.toml` with a `ci` profile, `Cargo.lock`, Dockerfile reading a `VERSION` build arg) and verify `cargo fmt --check`, `cargo clippy --locked -- -D warnings`, `cargo deny check`, and `cargo nextest run --profile ci` pass locally
- [x] 1.2 Add the fixture row to `tests/fixtures/mock-projects/README.md` and verify it names every workflow that uses it

## 2. Rust verification workflows

- [x] 2.1 Add `wf-rust-check.yml` (fmt, clippy `-D warnings`, cargo-deny, opt-in sqlx job) with `schemas/workflow-inputs/wf-rust-check.schema.json` and verify schema parity through `scripts/validate-workflows.cs`
- [x] 2.2 Add `wf-rust-test.yml` (nextest profile, Docker preflight, `SQLX_OFFLINE`, JUnit collection) with its schema and verify schema parity
- [x] 2.3 Add catalog sections with one Mermaid diagram each, Public Workflows and Permissions rows, README rows, and `.github/workflows/AGENTS.md` entries, and verify the catalog diagram check passes
- [x] 2.4 Add `docs/caching.md` Rust section (rust-cache default, sqlx, sccache alternative) and the Rust build-time version note, and verify the text matches the workflow inputs

## 3. Generic container workflows

- [x] 3.1 Add `wf-verify-publish-container-generic.yml` and `wf-publish-container-generic.yml` with schemas (no .NET stamping, `version-build-arg`), and verify `actionlint` and schema parity pass
- [x] 3.2 Extend the validator: job and step display names, publish concurrency, generic container contract (no `dotnet-setversion`, push only in publish, no environment in verify), shared build cache contract, verify-workflow list; verify the validator passes
- [x] 3.3 Add catalog sections, permission rows, README rows, `docs/caching.md` Docker note, and verify generated docs are current (`scripts/generate-docs.cs --check`)

## 4. Fixtures, policy, ADR

- [x] 4.1 Add local fixtures `tests/fixtures/workflow-contract/rust-workspace-local.yml` and a consumer fixture for the generic publish workflow, and verify `actionlint` accepts them
- [x] 4.2 Add `rust-check`, `rust-test`, and `rust-container` dogfood jobs to `release.yml`, include them in both release `needs:` lists, and verify the repository pipeline contract passes
- [x] 4.3 Add the three Rust actions to `policies/allowed-actions.yml` and write `docs/adr/0009-rust-workflows-and-generic-container.md`; verify both are referenced from the catalog or security docs
- [x] 4.4 Add a catalog consumer snippet (Rust Caller Pattern, generic publish example) plus `tests/fixtures/workflow-contract/rust-service-consumer.yml` for a MentionBot-shaped repository and verify it uses only documented inputs

## 5. Integration

- [x] 5.1 Run `dotnet run --file scripts/generate-docs.cs`, `dotnet run --file scripts/validate-workflows.cs`, and `actionlint`, and verify all pass
- [x] 5.2 Run `openspec validate add-rust-workflows --strict` and verify it passes

## Workflow follow-up

- Dogfood jobs prove the workflows on `daedalus` in the PR run.
- After merge and release, MentionBot replaces its local `rust` and `image` jobs.
- Archive the change after release.
