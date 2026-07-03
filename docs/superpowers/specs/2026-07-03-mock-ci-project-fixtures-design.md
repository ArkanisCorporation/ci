# Mock CI Project Fixtures Design

## Context

This repository provides reusable GitHub Actions workflows as its public API.
Current tests validate workflow contracts, schemas, docs, policies, and caller workflow fixtures.
The repository already has a small `tests/fixtures/node-pnpm` package, but it does not yet provide realistic multi-language fixture projects for end-to-end workflow smoke testing.
GitHub Actions documentation confirms that reusable workflows can be called from another repository with `owner/repo/.github/workflows/file.yml@ref` and from the same repository with `./.github/workflows/file.yml`.
This design uses same-repository calls for local smoke fixtures so workflow changes in this repository can be tested before release.

## Goals

Add real, small fixture projects under `tests/fixtures`.
Exercise shared workflows on actual TypeScript and .NET test data.
Keep fixtures deterministic, credential-free, and suitable for local or GitHub-hosted smoke validation.
Leave existing public `@v1` consumer contract fixtures intact.
Defer Python until a public Python setup workflow exists.

## Non-Goals

Do not add a Python mock project in this slice.
Do not publish packages or images.
Do not require NuGet, registry, cloud, or Kubernetes credentials.
Do not replace the existing static workflow contract validator.
Do not turn examples into full runnable sample repositories.

## Recommended Approach

Create runnable mock projects under `tests/fixtures/mock-projects`.
Create local caller workflow fixtures under `tests/fixtures/workflow-contract` that call `./.github/workflows/...`.
Use these local caller workflows as smoke-test entry points for `act` or future repository CI jobs.
Keep production-facing example docs and public `@v1` fixtures separate from local workflow-change fixtures.

## Fixture Projects

### TypeScript pnpm Fixture

Path: `tests/fixtures/mock-projects/typescript-pnpm`.
The fixture is a tiny TypeScript package with real lint, test, and build scripts.
It uses pnpm and a lockfile so `wf-setup-node.yml` can exercise package-manager setup, install, lint, test, build, metadata, and diagnostics.
The source will include one small exported function and a test that validates behavior.
The lint command will be lightweight and deterministic.

### .NET NuGet Library Fixture

Path: `tests/fixtures/mock-projects/dotnet-nuget-library`.
The fixture is a packable .NET library solution with one library project and one test project.
It targets .NET 10 to match the workflow defaults used by this repository.
It includes package metadata so `wf-verify-publish-nuget.yml` can pack a real `.nupkg` without publishing.
It also supports `wf-setup-dotnet.yml` for restore, format, build, test, coverage, metadata, and diagnostics.

### .NET Container App Fixture

Path: `tests/fixtures/mock-projects/dotnet-container-app`.
The fixture is a runnable .NET application with a Dockerfile.
It gives `wf-verify-publish-container-dotnet.yml` real project files to version-stamp and a real Docker context to build.
The Dockerfile will be small and use public .NET images.
The app will expose a simple deterministic behavior, such as a minimal HTTP endpoint or console output.

## Workflow Fixture Coverage

Add a local TypeScript caller fixture that invokes `./.github/workflows/wf-setup-node.yml`.
Set `working-directory` to the TypeScript fixture path.
Use `package-manager: pnpm`, a fixed package-manager major version, and diagnostics upload disabled for local `act` smoke fixtures.

Add a local .NET setup caller fixture that invokes `./.github/workflows/wf-setup-dotnet.yml`.
Point `solution` at the NuGet library fixture solution.
Keep permissions read-only unless coverage PR comments are intentionally enabled.

Add a local NuGet verification caller fixture that invokes `./.github/workflows/wf-verify-publish-nuget.yml`.
Point `project` at the library project and pass a fixed SemVer test version.
The workflow will build package artifacts but never publish them.

Add a local container verification caller fixture that invokes `./.github/workflows/wf-verify-publish-container-dotnet.yml`.
Point `context`, `dockerfile`, and version stamping inputs at the container app fixture.
Use a non-secret local image name and a fixed SemVer test version.
Each new workflow fixture will state its expected success or failure in comments.

## Validation

Run `dotnet run --file scripts/validate-workflows.cs` after the files are added.
Run package-level checks for the TypeScript fixture.
Run `dotnet test` for the NuGet library fixture.
Run `dotnet publish` for the container app fixture.
Run a bounded Docker build for the container app fixture when Docker is available.
Run a bounded `act` smoke test for at least one local fixture workflow when Docker and `act` are available.

## Documentation Updates

Update fixture or workflow documentation to describe the mock project paths and their intended workflow coverage.
Mention that Python remains deferred until the platform has a Python reusable workflow contract.
Keep Markdown sentences on separate lines for readability.

## Risks And Mitigations

Dependency lockfiles can become stale.
Mitigate this by keeping fixture dependencies minimal and lock-backed.

Local Docker availability can vary.
Mitigate this by making Docker smoke verification optional and documenting the exact command that was run or skipped.

Workflow smoke tests can become slow.
Mitigate this by keeping fixtures tiny and preserving the existing static validator as the default fast contract check.
