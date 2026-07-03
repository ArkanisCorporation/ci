# dotnet-webapi-container/AGENTS.md

## Scope

Example for ASP.NET/API container.

## Rules

- Build/test .NET before image build.
- Build image via Buildx.
- Publish digest + SBOM + provenance.
- Deploy examples consume digest only.
- No `latest` deploy to prod.
