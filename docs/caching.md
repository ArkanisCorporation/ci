# Caching

Audience: workflow implementer.

## Cache principles

- Cache dependencies, not truth.
- Cache keys include OS, arch, toolchain, lockfile, config.
- Avoid broad restore keys for untrusted PRs.
- Read-only cache on untrusted triggers reduces poisoning, but design must still be safe.
- Every reusable workflow that uses `runs-on/cache` must expose `enable-cache`.
- Set `enable-cache` to false for cold-restore validation, cache incident isolation, or runners without cache service access.
- Never cache secrets, credentials, kubeconfigs, `.npmrc` with token, NuGet API keys.
- Never cache `NUGET_AUTH_JSON`, `OP_SERVICE_ACCOUNT_TOKEN`, `GITHUB_ENV`, `RUNNER_TEMP`, generated `NuGetPackageSourceCredentials_*` values, generated `NUGET_AUTH_OP_*` values, generated 1Password env files, or generated host or Docker NuGet configs.

## .NET

Key components:

```text
nuget-{os}-{arch}-{dotnet-version}-{global-json-hash}-{packages-lock-hash}
```

Use locked restore in CI.
Cache NuGet's default folder as the literal `~/.nuget/packages` so the cache version is identical on every runner (ARK-502).
Skip the NuGet cache step when the runner advertises a persistent NuGet folder (`ARKANIS_PERSISTENT_NUGET_PACKAGES=true`, see `runner-contract.md`).
There the packages are already on disk: restoring re-downloads and re-extracts the whole multi-GB folder on every job, and after a key change or a failed restore every parallel job re-compresses and re-uploads it (`@actions/cache` builds the archive before it reserves the key, so a duplicate save still pays the compression).
`scripts/validate-workflows.cs` requires every NuGet `runs-on/cache` step to carry that gate.
Private NuGet restore credentials come from environment variables during restore only.
Do not cache generated NuGet auth files or temporary credential maps.

## Node

Key components:

```text
node-{os}-{arch}-{node-version}-{manager}-{lockfile-hash}
```

Strict installs only.
Node workflows cache the package-manager store with `runs-on/cache`.
The internal `setup-node` composite computes cache path from npm, pnpm, or yarn after package-manager setup.
Set `enable-cache` to false for cold-install validation or runners without cache service access.

## Rust

Last verified: 2026-10-31.

`wf-rust-check.yml` and `wf-rust-test.yml` cache with [`Swatinem/rust-cache`](https://github.com/Swatinem/rust-cache), gated by `enable-cache` like every other platform cache.

Key components (computed by rust-cache):

```text
rust-{job}-{key}-{os}-{arch}-{hash(rustc version, CARGO/RUST env, Cargo.lock, Cargo.toml, rust-toolchain)}
```

- `key` is `rust-check`, `rust-check-sqlx`, or `rust-test`, so lint and test builds (different profiles and features) never overwrite each other.
- Cached: `~/.cargo/registry`, `~/.cargo/git`, `~/.cargo/bin`, and the workspace `target/` directory. Before saving, rust-cache prunes workspace crates and stale build artifacts, which a plain `actions/cache` or `runs-on/cache` step of `target/` cannot do.
- `cache-on-failure` is false: a failed build never seeds the cache.
- Not cached: the toolchain under `~/.rustup`. A cold self-hosted job downloads it each run (about a minute on `daedalus`).
- rust-cache uses `@actions/cache` directly, not `runs-on/cache`. On `daedalus` that still lands on the LAN cache server, because the patched runner redirects every `actions/cache` client.
- Pull request caches are scoped to the pull request ref by GitHub, so a fork cannot poison the default-branch cache.

Set `enable-cache: false` for cold-build validation or runners without cache service access.

### sccache (alternative, not wired in)

[sccache](https://github.com/mozilla/sccache) caches individual rustc invocations instead of `target/`. It pays off when many jobs or matrix legs compile the same crates with different features, or when `target/` is too large to restore in full. Costs: a storage backend (GitHub Actions cache through `SCCACHE_GHA_ENABLED`, or S3/Redis), `RUSTC_WRAPPER=sccache`, and incremental compilation off. Adopt it only when rust-cache restore time or cache size becomes the bottleneck; it would be a new opt-in input, not a default change.

### sqlx

Committed `.sqlx/` metadata plus `sqlx-offline: true` lets clippy and tests compile without a database. The opt-in `sqlx-prepare-check` job is the only one that needs Postgres; it uses its own cache key because it compiles with `SQLX_OFFLINE` unset.

### Version stamping

Rust consumers stamp the release version at image build time: the container workflows pass `VERSION=<version>` as a build arg and the Dockerfile exposes it to rustc (for example `ENV APP_VERSION=$VERSION`, read with `option_env!`). `Cargo.toml` keeps a placeholder version and nothing is committed back, so no `@semantic-release/exec` or `@semantic-release/git` step is needed. Build args are part of the BuildKit cache key, so only layers after the `ARG` rebuild per release.

## Python

Key components:

```text
python-{os}-{arch}-{python-version}-{manager}-{lockfile-hash}
```

Prefer isolated venv; cache package download/build cache, not global interpreter state.

## Docker

- Use Buildx.
- Prefer registry cache for self-host/ARC portability.
- Use GHA cache for hosted-only, medium-size builds.
- Cache invalidation includes Dockerfile + lockfiles + build args that affect output.
- .NET and generic container workflows default to `type=gha` BuildKit cache when `enable-cache` is true and both `cache-from` and `cache-to` are empty.
- The generated cache scope is based on image, build context, Dockerfile, and platforms so independent container targets do not share one cache namespace.
- Set `enable-cache` to false for cold image-build validation or runners without GitHub cache service access.
- Set `cache-from` and `cache-to` when the caller needs registry cache, remote BuildKit portability, or a dedicated cache scope.
- When `nuget-build-secret` is true, the generated `NuGet.Config` is a BuildKit secret file and must not be included in cache keys, artifacts, build args, or image layers.
