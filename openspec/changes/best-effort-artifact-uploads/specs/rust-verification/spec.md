## MODIFIED Requirements

### Requirement: Opt-in sqlx offline metadata check

When `sqlx-prepare-check` is true, `wf-rust-check.yml` SHALL start a throwaway Postgres container
with `docker run` on the runner (no job `container:` and no `services:`), reach it on `127.0.0.1` or,
when the job runs inside a container, through the Docker bridge gateway, apply migrations from
`sqlx-migrations-source` when set, run `cargo sqlx prepare --check <sqlx-prepare-arguments>`, and
remove the container whatever the outcome. Container name and host port SHALL be unique per job so
parallel jobs on one Docker daemon do not collide. When false (default) no database is started.
`sqlx-container-image` is deprecated and ignored.

#### Scenario: Stale query metadata

- **WHEN** a query changed but `.sqlx/` was not regenerated and `sqlx-prepare-check` is true
- **THEN** the sqlx job fails

#### Scenario: Default skips the database

- **WHEN** a caller does not set `sqlx-prepare-check`
- **THEN** no Postgres container starts and no sqlx job runs

#### Scenario: Runner is a container on the host daemon

- **WHEN** the sqlx job runs on a runner where `/.dockerenv` exists
- **THEN** Postgres is reached through the bridge gateway address and the check runs

#### Scenario: Deprecated container image input

- **WHEN** a caller still passes `sqlx-container-image`
- **THEN** the workflow accepts it, ignores it, and emits a deprecation warning
