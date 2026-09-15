# setup-dotnet/AGENTS.md

## Scope

.NET SDK/NuGet setup helper.

## Rules

- Respect `global.json` when present.
- Support explicit `dotnet-version` input.
- Enable package cache only with lockfile-based key.
- Skip the package cache when the runner advertises a persistent NuGet folder (`ARKANIS_PERSISTENT_NUGET_PACKAGES=true` and no `NUGET_PACKAGES` override); see `docs/runner-contract.md`.
- Print `dotnet --info` to diagnostics.
- Never mutate NuGet sources with secrets unless caller passed explicit private restore or publish context.
- Restore guidance: `dotnet restore --locked-mode` for CI.
- Build guidance: `ContinuousIntegrationBuild=true`, deterministic, SourceLink-compatible.
