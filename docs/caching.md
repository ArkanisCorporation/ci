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

## .NET

Key components:

```text
nuget-{os}-{arch}-{dotnet-version}-{global-json-hash}-{packages-lock-hash}
```

Use locked restore in CI.

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
