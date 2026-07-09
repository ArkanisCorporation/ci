# setup-nuget-auth/AGENTS.md

## Scope

Private NuGet restore credential preparation for shared .NET workflows.

## Rules

- Treat `NUGET_AUTH_JSON`, resolved passwords, generated `NuGetPackageSourceCredentials_*` values, and generated credential files as secret material.
- Never print raw credentials, raw resolved auth JSON, generated NuGet credential strings, or generated `NuGet.Config` contents.
- Never invoke the 1Password `op` CLI directly.
- Resolve `op://` references only through the surrounding workflow's `1password/load-secrets-action@v4` step.
- Keep generated env files, map files, and Docker NuGet configs under `RUNNER_TEMP`.
- Generated Docker NuGet configs are BuildKit secret files only and must not be uploaded, cached, copied into images, or written to summaries.
