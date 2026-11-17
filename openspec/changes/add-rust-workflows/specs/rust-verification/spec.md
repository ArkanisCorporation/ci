## Purpose

Verifies a caller's Rust Cargo workspace (formatting, lints, dependency policy, sqlx offline
metadata, and tests) through reusable workflows, so Rust repositories share one CI contract.

## ADDED Requirements

### Requirement: Toolchain from the caller

Both Rust workflows SHALL install the toolchain the caller's `rust-toolchain.toml` (or
`rust-toolchain`) in `working-directory` selects, and SHALL fail when neither file exists rather
than silently using another toolchain.

#### Scenario: Pinned toolchain

- **WHEN** the caller's `rust-toolchain.toml` pins `channel = "1.99.1"`
- **THEN** rustfmt, clippy, and tests run with Rust 1.99.1

#### Scenario: Missing toolchain file

- **WHEN** `working-directory` has no `rust-toolchain.toml` or `rust-toolchain`
- **THEN** the job fails before running any cargo command and says which file is missing

### Requirement: Formatting check

`wf-rust-check.yml` SHALL run `cargo fmt --all --check` when `rustfmt` is true (default) and fail
on any formatting difference.

#### Scenario: Unformatted code

- **WHEN** a source file is not rustfmt-clean
- **THEN** the check job fails and the rustfmt log is uploaded with the diagnostics

### Requirement: Clippy denies warnings

`wf-rust-check.yml` SHALL run clippy with `clippy-arguments` (default
`--workspace --all-targets --all-features --locked`) and SHALL always pass `-D warnings`, so a
caller cannot disable warning denial through inputs.

#### Scenario: Clippy warning

- **WHEN** the workspace has one clippy warning
- **THEN** the check job fails

#### Scenario: Stale lockfile

- **WHEN** `Cargo.lock` does not match the manifests and default arguments are used
- **THEN** clippy fails because of `--locked`

### Requirement: Dependency policy

`wf-rust-check.yml` SHALL run `cargo deny check` (optionally limited by `cargo-deny-checks`) when
`cargo-deny` is true (default), using the caller's `deny.toml`.

#### Scenario: Disallowed licence

- **WHEN** a dependency's licence is not allowed by `deny.toml`
- **THEN** the check job fails

### Requirement: Opt-in sqlx offline metadata check

When `sqlx-prepare-check` is true, `wf-rust-check.yml` SHALL start a throwaway Postgres, apply
migrations from `sqlx-migrations-source` when set, and run `cargo sqlx prepare --workspace --check`.
When false (default) no database is started.

#### Scenario: Stale query metadata

- **WHEN** a query changed but `.sqlx/` was not regenerated and `sqlx-prepare-check` is true
- **THEN** the sqlx job fails

#### Scenario: Default skips the database

- **WHEN** a caller does not set `sqlx-prepare-check`
- **THEN** no Postgres service starts and no sqlx job runs

### Requirement: Tests with nextest

`wf-rust-test.yml` SHALL run `cargo nextest run --profile <nextest-profile>` (default profile
`ci`) with `nextest-arguments` and SHALL fail when any test fails after the profile's retries.

#### Scenario: Failing test

- **WHEN** one test fails on every attempt
- **THEN** the test job fails and the nextest log plus any JUnit report are uploaded

### Requirement: Docker for integration tests

When `require-docker` is true, `wf-rust-test.yml` SHALL verify a reachable Docker daemon before
running tests and fail early with a diagnostic otherwise.

#### Scenario: No Docker daemon

- **WHEN** `require-docker` is true and `docker info` fails
- **THEN** the job fails before compiling tests

### Requirement: sqlx offline mode

When `sqlx-offline` is true, both workflows SHALL export `SQLX_OFFLINE=true` to cargo commands
that compile the workspace (clippy and tests), so compile-time query checks use committed `.sqlx/`
metadata.

#### Scenario: No database during tests compile

- **WHEN** `sqlx-offline` is true and no `DATABASE_URL` is set
- **THEN** sqlx macros compile against `.sqlx/` instead of failing

### Requirement: Prebuilt tool binaries

Cargo subcommands (cargo-deny, cargo-nextest, sqlx-cli) SHALL be installed from prebuilt binaries
at the versions the inputs pin, and SHALL NOT be compiled from source; a missing prebuilt binary
SHALL fail the job.

#### Scenario: Pinned tool version

- **WHEN** `nextest-version` is `0.9.150`
- **THEN** cargo-nextest 0.9.150 is installed from a release binary and recorded in run metadata

### Requirement: Runner contract and observability

Both workflows SHALL expose `runs-on`, `runs-on-json`, `runs-on-self-hosted`, `enable-cache`,
`artifact-retention-days`, `upload-diagnostics`, and `timeout-minutes`, run with `contents: read`
only, and write a step summary, `run-metadata.json`, `artifact-manifest.json`, and diagnostics.

#### Scenario: Cold run

- **WHEN** `enable-cache` is false
- **THEN** no Rust build cache is restored or saved and the job still succeeds

#### Scenario: Self-hosted preflight

- **WHEN** `runs-on-self-hosted` is true
- **THEN** disk, workspace, and tool availability are recorded before cargo runs
