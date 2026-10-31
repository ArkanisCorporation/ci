# ADR-0009: Rust verification workflows and generic container publishing

Status: accepted

Audience: platform maintainers, security reviewers, and Rust or Dockerfile-only consumers.
Last verified: 2026-10-31.

## Context

MentionBot (ARK-678) is the first Arkanis Rust service. This platform had no Rust workflows, and its only container workflows stamp `.csproj` versions before Docker Buildx (ADR-0002), so a repository without .NET projects could not use them.
ADR-0002 already noted that generic image publishers need a future non-.NET workflow.
The dogfood runner pool, `daedalus`, has no preinstalled Rust, runs each runner inside a container with the host Docker socket, and advertises a shared BuildKit through `BUILDKIT_HOST`.

## Decision

- **New public workflows** `wf-rust-check.yml` (rustfmt, clippy, cargo-deny, opt-in sqlx prepare check) and `wf-rust-test.yml` (cargo-nextest).
- **Toolchain** from the caller's `rust-toolchain.toml` via `actions-rust-lang/setup-rust-toolchain`; no toolchain input, and a missing file fails the job instead of falling back to stable.
- **Clippy always denies warnings.** `-D warnings` is appended after the caller's `clippy-arguments`.
- **Tools from prebuilt binaries only.** `taiki-e/install-action` with `fallback: none` for cargo-deny and cargo-nextest; sqlx-cli through `cargo binstall --disable-strategies compile`. A missing binary fails the job.
- **Cache** with `Swatinem/rust-cache`, gated by `enable-cache`, one key per job kind. sccache is documented as an alternative, not wired in.
- **sqlx prepare check** runs in a separate job, only when `sqlx-prepare-check` is true, inside a job container next to a Postgres service, so the database is reachable by service name on hosted runners and on container runners.
- **New generic container pair** `wf-verify-publish-container-generic.yml` and `wf-publish-container-generic.yml`: the .NET pair without stamping, NuGet credentials, or SDK inputs. The release version reaches the build only as the `version-build-arg` build argument (default `VERSION`). The .NET pair is unchanged. The pre-ADR-0002 name `wf-publish-container.yml` stays reserved.
- **Publish permissions** for the generic pair are `contents: read` and `packages: write`. Buildx provenance and SBOM attestations need no OIDC or attestations scope. GitHub artifact attestations are out of scope.
- **Versioning for Rust consumers** is stamped at image build time; nothing is committed back, so the existing ban on `@semantic-release/exec` stays as it is (`allow-exec-plugin` remains the escape hatch).
- **No `rust` runner label.** The workflows install their own toolchain, so no capability label is needed.
- **Action references** use exact version tags (`@v2.0.0`, `@v2.9.3`, `@v2.88.1`) per the security model; the three actions are added to `policies/allowed-actions.yml`.

## Consequences

Rust and other Dockerfile-only repositories adopt CI with `uses:` lines and no local jobs.
Cold self-hosted Rust jobs download the toolchain every run because `~/.rustup` is not cached.
The Buildx, tag, and cache steps now exist twice (.NET and generic); the validator applies the same cache contract to both pairs. Extracting a shared composite is a later refactor.
The sqlx check compiles the workspace a second time with `SQLX_OFFLINE` unset, so it is opt-in.

## Migration

None for existing callers. New consumers:

- Replace local `cargo fmt`/`clippy`/`deny` jobs with `wf-rust-check.yml`, and local nextest jobs with `wf-rust-test.yml`.
- Replace local image jobs with `wf-verify-publish-container-generic.yml` on pull requests and `wf-publish-container-generic.yml` after semantic-release, passing `new-version` to `version` and `new-tag` to `version-tag`.
- Declare `ARG VERSION` in the Dockerfile stage that compiles and expose it to the build.

## References

- [workflow-catalog.md](../workflow-catalog.md) Rust and generic container sections.
- [caching.md](../caching.md#rust).
- actions-rust-lang/setup-rust-toolchain: https://github.com/actions-rust-lang/setup-rust-toolchain
- Swatinem/rust-cache: https://github.com/Swatinem/rust-cache
- taiki-e/install-action: https://github.com/taiki-e/install-action
- GitHub service containers: https://docs.github.com/en/actions/tutorials/use-containerized-services/use-docker-service-containers

Related: ARK-678, ARK-687.
