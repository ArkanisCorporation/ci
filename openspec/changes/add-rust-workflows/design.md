# Design

## Context

See proposal.md - Why. Constraints that shape the approach:

- `scripts/validate-workflows.cs` requires every external `uses:` to reference a version tag
  (`vN`, `vN.N`, `vN.N.N`), and `docs/security-model.md` makes tags the default with SHA pins
  reserved for incidents. A SHA-pinned action fails validation.
- Every public workflow takes the runner trio (`runs-on`, `runs-on-json`, `runs-on-self-hosted`),
  writes a summary, `run-metadata.json`, `artifact-manifest.json`, and uploads diagnostics.
- This repository dogfoods its workflows on `daedalus` (self-hosted, runner itself in a container,
  Docker reached through the host socket, `BUILDKIT_HOST` advertised, `actions/cache` redirected
  to a LAN cache server). `daedalus` has no preinstalled Rust.
- MentionBot shape: Cargo workspace, `rust-toolchain.toml` 1.99.1 (also lists IDE components),
  `deny.toml`, `.config/nextest.toml` with a `ci` profile, sqlx 0.9 with committed `.sqlx/`,
  testcontainers 0.27 integration tests, Dockerfile using cargo-chef, and the release version
  injected at image build time.

## Trust zones

| Workflow | Zone | Token |
|---|---|---|
| `wf-rust-check.yml` | untrusted or trusted-build | `contents: read` |
| `wf-rust-test.yml` | untrusted or trusted-build | `contents: read` |
| `wf-verify-publish-container-generic.yml` | untrusted or trusted-build | `contents: read` |
| `wf-publish-container-generic.yml` | publish (environment-gated) | `contents: read`, `packages: write` |

Hosted vs self-hosted: no step is hosted-only. Self-hosted runs add a preflight
(`if: inputs.runs-on-self-hosted`) recording disk, workspace, `rustup`/`docker` availability, and
the container workflows read `BUILDKIT_HOST` only when self-hosted, exactly as the .NET pair does.

## Goals / Non-Goals

**Goals:** one-file adoption for MentionBot-shaped repositories; work unchanged on hosted
`ubuntu-latest` and on `daedalus`; no compile-from-source tool installs.

**Non-Goals:** cross-compilation targets, multiple toolchains per run, coverage (`cargo llvm-cov`),
crate publishing, semantic-release changes, generalising the .NET container workflows.

## Decisions

### D1. Version tags, not SHA pins

The brief asked for SHA-pinned third-party actions. This repository's validator rejects anything
but version tags, and its security model documents tags as the default. Decision: pin exact
release tags (`@v2.88.1`, `@v2.9.3`, `@v2.0.0`), matching existing exact pins such as
`runs-on/cache@v5.0.7`. Changing the platform-wide pinning policy is a separate security decision.

### D2. Toolchain via `actions-rust-lang/setup-rust-toolchain`

It reads `rust-toolchain.toml` (`rustup toolchain install`), installs rustup when the runner lacks
it (`daedalus`), and supports a sub-directory (`rust-src-dir`). Inputs fixed by the workflow:
`cache: false` (D3 owns caching), `rustflags: ""` and `build-warnings: ""` (do not override the
caller's cargo config; clippy denies warnings explicitly), `matcher: true`.
Alternatives: `dtolnay/rust-toolchain` ignores `rust-toolchain.toml` (the toolchain is the ref or
an input), so the caller's pin would be duplicated in CI; plain `rustup` fails on `daedalus`,
which has no rustup. A pre-step fails when no toolchain file exists, because the action would otherwise
fall back to `stable` silently.

### D3. Caching: `Swatinem/rust-cache` behind `enable-cache`

Rust-cache keys on toolchain, lockfile, and job, and prunes stale build artifacts before saving,
which a generic `runs-on/cache` step cannot do for `target/`. It goes through `@actions/cache`, so
on `daedalus` it lands on the LAN cache server like every other cache. `workspaces` is set from
`working-directory`, `cache-on-failure: false`. The validator's `runs-on/cache` gate does not
apply, but the step is still gated with `if: inputs.enable-cache`. sccache is documented in
`docs/caching.md` as the alternative for fan-out or very large workspaces; not wired in.

### D4. Tools from prebuilt binaries

`taiki-e/install-action@v2.88.1` with `tool: cargo-deny@<v>` / `cargo-nextest@<v>` and
`fallback: none`, so an unknown tool or version fails instead of compiling. sqlx-cli is not in
install-action's manifest; the workflow installs `cargo-binstall` the same way and runs
`cargo binstall --no-confirm --disable-strategies compile sqlx-cli@<v>` (quickinstall publishes
`sqlx-cli-0.9.0`). Versions are inputs with pinned defaults (cargo-deny 0.20.2, nextest 0.9.150,
sqlx-cli 0.9.0, cargo-binstall 1.26.1).

### D5. sqlx prepare check: separate opt-in job in a container

`cargo sqlx prepare --check` needs a live Postgres. Job `services:` cannot be made conditional
per step, and on `daedalus` the runner is itself a container, so a service published on the
Docker host is not reachable on `localhost`. Decision: a second job, `sqlx`, gated by
`if: inputs.sqlx-prepare-check`, running in a `container:` (`sqlx-container-image`, default
`rust:1-trixie`) with a `postgres` service; GitHub puts both on one network, so
`DATABASE_URL=postgres://postgres:postgres@postgres:5432/postgres` works on hosted and DooD
runners alike. Migrations run with `sqlx migrate run --source <sqlx-migrations-source>` when set.
Default `sqlx-postgres-image` is `postgres:17-alpine` (MentionBot's testcontainers tag).
Trade-off: an extra job and a full compile when enabled, so it is opt-in.

### D6. Docker for testcontainers

No setup action: hosted Ubuntu ships Docker; `daedalus` mounts the host socket and testcontainers
0.27 resolves the bridge gateway when it detects `/.dockerenv`. `require-docker` (default `false`)
runs `docker info` first so a missing daemon fails in seconds instead of after compilation.

### D7. New generic container pair instead of generalising the .NET one

Making `.NET` stamping optional in `wf-publish-container-dotnet.yml` would mean a new default
(stamp off) that breaks callers, or a flag that leaves NuGet and SDK inputs dangling on the
generic path. ADR-0002 already anticipated "a future non-.NET workflow". Decision: new
`wf-publish-container-generic.yml` and `wf-verify-publish-container-generic.yml` copied from the
.NET pair minus stamping, NuGet secrets, and SDK inputs. `wf-publish-container.yml` is not reused:
it is the pre-ADR-0002 name the validator keeps reserved so stale callers fail loudly.
Duplication of the Buildx/cache/tag steps is accepted; extracting a composite is a later refactor.

### D8. Version build arg

`version-build-arg` (default `VERSION`, same as the .NET pair) names the build arg that carries
`version`; empty disables it; a caller `build-args` entry of the same name wins. MentionBot maps it
to a compile-time env (for example `ARG VERSION` then `ENV APP_VERSION=$VERSION` before
`cargo build`, read with `option_env!`).

### D9. Publish permissions

The .NET publish job also requests `id-token: write` and `attestations: write`, but buildx
provenance and SBOM attestations need neither. The generic publish job requests only
`contents: read` and `packages: write`. GitHub artifact attestations
(`actions/attest-build-provenance`) are out of scope: private repositories on the organisation's
GitHub Free plan cannot use them.

### D10. No `rust` runner label

`policies/runner-labels.yml` labels describe preinstalled capabilities. These workflows install
their own toolchain, so `self_hosted_linux` (plus `docker` for tests that need it) is enough.
`daedalus` is used through `RUNNER_DEFAULT` like every other dogfood job.

## Risks / Trade-offs

- [Rust toolchain download on every cold `daedalus` job] -> rust-cache restores `~/.cargo` and
  `target/`, not `~/.rustup`; accepted, about a minute per job. A persistent rustup dir on the
  runner pool is a ci-runners follow-up if it hurts.
- [setup-rust-toolchain installs rustup with `curl | sh` from `sh.rustup.rs`] -> official
  installer, only on runners without rustup; documented.
- [IDE components in `rust-toolchain.toml` (rust-analyzer, rust-src) are downloaded in CI] ->
  small; callers can drop them or use a CI-specific toolchain file.
- [quickinstall stops publishing a sqlx-cli build] -> install fails loudly (no compile fallback);
  bump `sqlx-cli-version` or disable the check.
- [Copied container steps drift from the .NET pair] -> validator applies the same cache contract
  to both pairs; catalog documents them side by side.

## Migration Plan

Additive. After merge, semantic-release publishes a minor release and moves `v1`. MentionBot then
replaces its local `rust` and `image` jobs with the four `uses:` lines. Rollback: callers revert
to their local jobs; the workflows can be removed in a later major.
