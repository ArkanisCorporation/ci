# Mock Project Fixtures

These projects are real, small consumers for local workflow smoke tests.
They live under `tests/fixtures` so they can exercise the shared workflows without turning `examples` into runnable sample repositories.

| Fixture | Purpose | Workflows |
|---|---|---|
| `typescript-pnpm` | TypeScript package with pnpm lint, test, and build scripts. | `wf-setup-node.yml` |
| `dotnet-nuget-library` | .NET 10 library plus xUnit tests and package metadata. | `wf-setup-dotnet.yml`, `wf-verify-publish-nuget.yml` |
| `dotnet-container-app` | Runnable .NET 10 ASP.NET Core app with Dockerfile. | `wf-verify-publish-container-dotnet.yml` |

Local workflow callers live in `tests/fixtures/workflow-contract/*-local.yml`.
They call `./.github/workflows/...` so changes in this repository can be tested before a stable `vN` release.
The repository `release.yml` workflow calls these fixtures before semantic-release verification or publication.

Python is intentionally deferred.
Add a Python fixture after the platform has a public Python reusable workflow contract.
