# ADR-0006: Private NuGet restore credentials

Status: accepted

## Context

Shared .NET workflows need to restore from private NuGet package sources before build, test, pack, container build, or Aspire deploy commands can run.
Reusable workflow secret names are static, but consumers may need one or many private feeds.
`actions/setup-dotnet` supports an authenticated `source-url` path, but that path is single-source oriented and does not cover Dockerfile restores.
Dockerfile restores need credentials inside BuildKit without copying them into image layers.
Some callers store package credentials in 1Password and want CI to resolve `op://` item references without invoking the `op` CLI directly.
Last verified: 2026-07-09.

## Decision

Expose one optional reusable workflow secret named `NUGET_AUTH_JSON` on shared .NET restore-capable workflows.
Expose one optional reusable workflow secret named `OP_SERVICE_ACCOUNT_TOKEN` for callers whose `NUGET_AUTH_JSON` contains `op://` references.
Represent multiple package source credentials as `sources` entries inside `NUGET_AUTH_JSON`.
Allow literal values, `op://` references, `github://actor`, and `github://token` in credential fields.
Resolve `github://actor` from `github.actor`.
Resolve `github://token` from `github.token`, passed explicitly to auth steps as `GITHUB_TOKEN_FOR_NUGET_AUTH`.
Resolve `op://` values through a generated env file loaded by `1password/load-secrets-action@v4`.
Never invoke the 1Password `op` CLI directly.
For host restore, write masked `NuGetPackageSourceCredentials_{name}` values to `GITHUB_ENV` only for the restore window.
For Dockerfile restore, write a temporary `NuGet.Config` under `RUNNER_TEMP` and pass it to Docker Buildx as a `secret-files` entry.
Delete generated env files, map files, and Docker NuGet configs in `if: always()` cleanup steps.

## Consequences

Current unauthenticated consumers keep working because both secrets are optional.
Callers can provide any number of private feed credentials without adding fixed `NUGET_TOKEN_1` style workflow secrets.
Callers must keep non-secret package source URLs in committed `NuGet.Config` files for host restore.
Callers should use NuGet package source mapping to reduce dependency confusion risk when multiple feeds are configured.
Docker consumers must opt in with `nuget-build-secret: true` and update Dockerfiles to mount the `nuget_config` BuildKit secret during restore.
The `OP_SERVICE_ACCOUNT_TOKEN` secret is available only to trusted workflows and is not passed to Docker builds.
The generated Docker NuGet config contains clear-text credentials by design, but only as a temporary BuildKit secret file under `RUNNER_TEMP`.

## Migration

For host restore, add a committed `NuGet.Config` with private package source keys.
Create `NUGET_AUTH_JSON` in the caller repository or organization secrets.
Pass it explicitly to the reusable workflow as `NUGET_AUTH_JSON`.
When using 1Password references, also pass `OP_SERVICE_ACCOUNT_TOKEN`.
For GitHub Packages in the same access boundary, use `github://actor` and `github://token`.
For Dockerfile restore, set `nuget-build-secret: true` and mount `id=nuget_config` in the Dockerfile restore step.

## References

- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub secrets in conditionals: https://docs.github.com/actions/security-guides/using-secrets-in-github-actions
- GitHub contexts reference: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- GitHub Packages with Actions: https://docs.github.com/en/packages/managing-github-packages-using-github-actions-workflows/publishing-and-installing-a-package-with-github-actions
- NuGet authenticated feeds: https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds
- NuGet config reference: https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file
- Docker Buildx secrets: https://docs.docker.com/build/ci/github-actions/secrets/
- Docker build-push action: https://github.com/docker/build-push-action
- 1Password GitHub Actions integration: https://www.1password.dev/ci-cd/github-actions
- 1Password load-secrets-action: https://github.com/1Password/load-secrets-action
