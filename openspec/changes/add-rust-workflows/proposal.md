# Proposal

## Why

MentionBot (ARK-678) is the first Arkanis Rust service, and this platform has no Rust support: its
only container workflow stamps `.csproj` versions, so a Dockerfile-only repository cannot use it.
MentionBot therefore carries local `rust` and `image` jobs. ARK-687 moves that work into shared
`wf-*` workflows so the next Rust (or any Dockerfile) repository adopts it with one caller file.

## What Changes

- **New `wf-rust-check.yml`**: rustfmt check, clippy with `-D warnings` (all targets, `--locked` by
  default), cargo-deny, and an opt-in `cargo sqlx prepare --check` job against a throwaway
  Postgres service. Toolchain comes from the caller's `rust-toolchain.toml`.
- **New `wf-rust-test.yml`**: cargo-nextest with a caller-selected profile (default `ci`), Docker
  available for testcontainers, optional `SQLX_OFFLINE=true`, JUnit report collection when the
  profile writes one.
- Both install tools from prebuilt binaries (`taiki-e/install-action` with `fallback: none`,
  `cargo-binstall` with the compile strategy disabled for `sqlx-cli`) and cache with
  `Swatinem/rust-cache` behind the standard `enable-cache` input.
- **New `wf-verify-publish-container-generic.yml` and `wf-publish-container-generic.yml`**: the
  existing .NET container pair without .NET stamping or NuGet credentials. Buildx, GHCR login,
  buildx provenance and SBOM attestations, tags from the release version, BuildKit cache and
  remote-BuildKit handling unchanged. The release version reaches the Dockerfile as a build arg
  (`VERSION` by default, renamable with `version-build-arg`), which is how Rust consumers stamp the
  version at build time.
- The .NET container workflows are **unchanged**; existing callers are unaffected.
- Contract files for all four: input schemas, catalog sections and diagrams, README rows,
  workflows `AGENTS.md` list, validator entries, a Rust mock project, local fixtures, and dogfood
  jobs in `release.yml`.
- **Docs**: Rust caching section (rust-cache default, sqlx and sccache as the alternative), a
  Rust-consumer note that versions are stamped at image build time rather than committed back,
  allowed-actions policy additions, ADR-0009.

Out of scope: semantic-release changes. `allow-exec-plugin` already exists on both release
workflows, and MentionBot no longer commits versions back (no `cargo set-version`, no
`@semantic-release/git`). Also out of scope: a `rust` runner label (none is needed, see design),
GitHub artifact attestations (`actions/attest-build-provenance`), and crate publishing.

## Capabilities

### New Capabilities

- `rust-verification`: reusable Rust lint, policy, and test verification (`wf-rust-check.yml`,
  `wf-rust-test.yml`).
- `container-publish-generic`: reusable Dockerfile image verification and publishing with no
  language-specific version stamping.

### Modified Capabilities

None.

## Impact

- New public API: four `wf-*.yml` workflows plus schemas. Additive only, ships as a `feat:` minor
  release on `v1`.
- New third-party actions: `actions-rust-lang/setup-rust-toolchain`, `Swatinem/rust-cache`,
  `taiki-e/install-action` (added to `policies/allowed-actions.yml`).
- `release.yml` gains three fixture dogfood jobs on `daedalus`, so pull requests here take
  longer (Rust toolchain download plus a small image build).
- Consumer: MentionBot can replace its local `rust` and `image` jobs after the release.
