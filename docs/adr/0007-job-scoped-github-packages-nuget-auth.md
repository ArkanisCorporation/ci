# ADR-0007: Job-scoped GitHub Packages NuGet authentication

Status: accepted

Audience: platform maintainers, security reviewers, and .NET workflow consumers.
Last verified: 2026-09-26.

## Context

ADR-0006 allows `github://actor` and `github://token` references inside `NUGET_AUTH_JSON`.
Those references resolve to the workflow's job-scoped identity, so requiring a caller to store or forward a JSON secret for a GitHub-only feed adds configuration without supplying a credential.
The caller's `NuGet.Config` already contains the package source URL and its source name, which can include punctuation or spaces.
Reusable workflows receive the caller's `github.token`, subject to the caller and called job permission ceilings.

## Decision

Add an opt-in `github-packages-auth` input, defaulting to false, to .NET workflows that restore packages.
When enabled, read the caller's `NuGet.Config` and select only effective package sources whose HTTPS URL is exactly `https://nuget.pkg.github.com/<caller-owner>/index.json`.
Reject lookalike hosts, other owners, query strings, fragments, duplicate effective source keys, and missing matches.
Use the matched source's existing key and resolve `github.actor` and the job-scoped `github.token` at runtime.
For host restore, use a temporary copy of the caller config with credentials under `RUNNER_TEMP`, including when the source name could be represented as an environment variable.
For Dockerfile restore, use the existing BuildKit `nuget_config` secret mount and require `nuget-build-secret: true`.
Delete generated configs after use, keep credentials out of outputs and artifacts, and reject fork pull requests before binding the token.
Keep `NUGET_AUTH_JSON` and 1Password support for other feeds; combining them with GitHub Packages auth requires distinct source names.

## Consequences

GitHub-only callers need no stored restore token or JSON secret.
They must grant `packages: read` at every reusable workflow caller layer, and the package must grant the caller repository Actions access.
Sources owned by another account are not discovered automatically; callers can use explicit `NUGET_AUTH_JSON` references for them.
Untrusted fork pull requests cannot run credentialed GitHub Packages restore.

## Migration

Commit the non-secret GitHub Packages source to the caller's `NuGet.Config`.
Set `github-packages-auth: true` on each restoring reusable workflow call and forward it through any caller-owned wrappers.
Set `nuget-source-config-path` if the config is outside the repository root.
For container builds, also set `nuget-build-secret: true` and mount `id=nuget_config` during Dockerfile restore.
Remove GitHub-only `NUGET_AUTH_JSON` forwarding after verifying package access and a trusted workflow run.
Retain explicit `NUGET_AUTH_JSON` for other private feeds.

## References

- GitHub reusable workflow token context: https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations
- GitHub Packages permissions and Actions access: https://docs.github.com/en/packages/learn-github-packages/about-permissions-for-github-packages
- GitHub NuGet registry: https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry
- NuGet config reference: https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file
- Docker Buildx secrets: https://docs.docker.com/build/ci/github-actions/secrets/
