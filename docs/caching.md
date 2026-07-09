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
- Never cache `NUGET_AUTH_JSON`, `OP_SERVICE_ACCOUNT_TOKEN`, `GITHUB_ENV`, `RUNNER_TEMP`, generated `NuGetPackageSourceCredentials_*` values, generated `NUGET_AUTH_OP_*` values, generated 1Password env files, or generated Docker NuGet configs.

## .NET

Key components:

```text
nuget-{os}-{arch}-{dotnet-version}-{global-json-hash}-{packages-lock-hash}
```

Use locked restore in CI.
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
- .NET container workflows default to `type=gha` BuildKit cache when `enable-cache` is true and both `cache-from` and `cache-to` are empty.
- The generated cache scope is based on image, build context, Dockerfile, and platforms so independent container targets do not share one cache namespace.
- Set `enable-cache` to false for cold image-build validation or runners without GitHub cache service access.
- Set `cache-from` and `cache-to` when the caller needs registry cache, remote BuildKit portability, or a dedicated cache scope.
- When `nuget-build-secret` is true, the generated `NuGet.Config` is a BuildKit secret file and must not be included in cache keys, artifacts, build args, or image layers.
