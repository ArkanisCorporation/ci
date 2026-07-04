# complex-app/AGENTS.md

## Scope

Use these instructions for repositories with multiple products, deploy targets, signing, or release-side scans.
Known examples are CitizenId and ArkanisOverlay.

## Goal

Extract common verification, release metadata, package publishing, image publishing, and deployment setup without flattening app-specific lanes.

## Inspection Order

1. Read build, test, release, publish, deploy, signing, scan, and cleanup workflows.
2. Map each job to a trust zone.
3. Identify which jobs require Windows, self-hosted runners, cluster access, signing credentials, or third-party upload tokens.
4. Identify artifacts crossing from verification to publish or deploy.

## Target Shape

- Use `wf-dotnet-format.yml`, `wf-dotnet-test.yml`, `wf-node-lint.yml`, `wf-node-test.yml`, and `wf-node-build.yml` for verification lanes.
- Use `wf-setup-dotnet-jetbrains.yml` when a repository already gates JetBrains CleanupCode or Rider/ReSharper cleanup conventions.
- Use `wf-release-semantic.yml` only for release metadata.
- Use `wf-publish-nuget.yml` and `wf-publish-container-dotnet.yml` for .NET publish lanes.
- Use bare `new-version` for package and assembly stamping.
- Use tagged `new-tag` only for image or deployment tags.
- Use `wf-deploy-k8s-aspire.yml` for Aspire Kubernetes deploys where applicable.
- Keep signing, VirusTotal, Coolify, compose, and custom app deployment as separate jobs with explicit environments.

## Rules

- Do not merge signing, scanning, image publishing, package publishing, or deploy into semantic-release.
- Do not treat Windows-only work as a generic setup concern.
- Do not move secrets into outputs, summaries, artifacts, or cache.
- Do not replace self-hosted deploy labels with hosted runners.
- Do not allow mutable image tags to cross into production deploy without documented exception.

## Verification

- Run each verification lane independently.
- Check artifact manifest and release outputs before publish jobs.
- Use `wf-verify-publish-*` and `wf-verify-deploy-*` workflows for publish and deploy validation where available.
- Confirm environment protection exists for signing, production deploy, and third-party upload jobs.
