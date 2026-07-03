# modern-dotnet-package/AGENTS.md

## Scope

Use these instructions for modern .NET package repositories.
Known examples are Template.NET, Aspire.Hosting.Extensions.Kubernetes, and aspire-kubernetes-example-host.

## Goal

Normalize nearly modern package workflows onto this platform.
Keep repository-specific package metadata and release channels intact.

## Inspection Order

1. Read `_test.yaml`, `_release.yaml`, `main.yaml`, `pipeline-quality.yaml`, and any local setup action.
2. Identify package projects, pack settings, release branches, NuGet publish mode, and backpropagation logic.
3. Compare local setup action behavior against `wf-setup-dotnet.yml`.

## Target Shape

- Use `wf-setup-dotnet.yml` for verification.
- Use `wf-setup-dotnet-jetbrains.yml` only when the repository already gates JetBrains CleanupCode.
- Use `wf-release-semantic.yml` for semantic-release metadata.
- Use `wf-publish-nuget.yml` for package publication.
- Keep package backpropagation separate.
- Keep pipeline quality validation for workflow and release config checks.

## Rules

- Do not keep local setup actions when platform setup covers the behavior.
- Do not keep `@semantic-release/exec` for pack or publish.
- Do not publish NuGet packages from the semantic-release job.
- Do not weaken Trusted Publishing or API-key fallback checks.
- Do not remove package-specific SourceLink, symbols, or deterministic build settings without evidence.

## Verification

- Run package build and pack locally where possible.
- Run workflow static checks.
- Verify produced package version comes from release output.
- Verify package publish job is skipped when release output says no release.
