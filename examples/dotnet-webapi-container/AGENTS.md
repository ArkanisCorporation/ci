# dotnet-webapi-container/AGENTS.md

## Scope

Example for ASP.NET/API container.

## Rules

- Build/test .NET before image publishing.
- Stamp .NET project versions before Buildx when publishing release images.
- Publish image via Buildx.
- Publish digest + SBOM + provenance.
- Deploy examples consume digest only.
- No `latest` deploy to prod.
