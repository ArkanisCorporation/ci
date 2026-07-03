# References

Last verified: 2026-07-03.

## Primary Docs

- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub reusable workflow configuration comparison: https://docs.github.com/en/actions/concepts/workflows-and-actions/reusing-workflow-configurations
- GitHub workflow syntax: https://docs.github.com/actions/using-workflows/workflow-syntax-for-github-actions
- GitHub contexts reference: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- GitHub secrets in conditionals: https://docs.github.com/actions/security-guides/using-secrets-in-github-actions
- GitHub composite action metadata: https://docs.github.com/en/actions/reference/workflows-and-actions/metadata-syntax
- GitHub composite action tutorial: https://docs.github.com/en/actions/tutorials/create-actions/create-a-composite-action
- GitHub secure use reference: https://docs.github.com/en/actions/reference/security/secure-use
- GitHub artifacts: https://docs.github.com/en/actions/tutorials/store-and-share-data
- actions/setup-node: https://github.com/actions/setup-node
- Docker build-push action: https://github.com/docker/build-push-action
- dotnet tool install: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install
- dotnet-setversion NuGet package: https://www.nuget.org/packages/dotnet-setversion
- JetBrains ReSharper command line tools: https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html
- JetBrains CleanupCode: https://www.jetbrains.com/help/resharper/CleanupCode.html
- JetBrains InspectCode: https://www.jetbrains.com/help/resharper/InspectCode.html
- ReportGenerator: https://github.com/danielpalme/ReportGenerator
- actionlint: https://github.com/rhysd/actionlint
- GitHub CLI pull request commands: https://cli.github.com/manual/gh_pr
- semantic-release configuration: https://semantic-release.gitbook.io/semantic-release/usage/configuration
- semantic-release GitHub plugin: https://github.com/semantic-release/github
- Node.js Corepack: https://nodejs.org/api/corepack.html
- GitHub local reusable workflow reference changelog: https://github.blog/changelog/2022-01-25-github-actions-reusable-workflows-can-be-referenced-locally/
- NuGet Trusted Publishing: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
- NuGet/login action: https://github.com/NuGet/login
- nektos/act: https://github.com/nektos/act
- actionlint: https://github.com/rhysd/actionlint

## Current Platform Changes To Monitor

- Node20 to Node24 runner runtime migration: https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/
- Parallel and background steps: https://github.blog/changelog/2026-06-25-actions-steps-can-now-be-run-in-parallel/
- Read-only cache for untrusted triggers: https://github.blog/changelog/2026-06-26-read-only-actions-cache-for-untrusted-triggers/
- Non-zipped artifacts: https://github.blog/changelog/2026-02-26-github-actions-now-supports-uploading-and-downloading-non-zipped-artifacts/
- Self-hosted runner minimum version enforcement: https://github.blog/changelog/2026-06-12-github-actions-minimum-version-enforcement-timeline-for-self-hosted-runners/
- Safer `pull_request_target` defaults: https://github.blog/changelog/2026-06-18-safer-pull_request_target-defaults-for-github-actions-checkout/

## Known Edge Discussions

- Composite actions inside external reusable workflows need explicit repository references or checked-out platform code: https://github.com/orgs/community/discussions/18601
- Local composite actions resolve against the checked-out caller workspace: https://github.com/actions/runner/issues/1348
- NuGet Trusted Publishing with shared workflows can require careful policy ownership: https://github.com/NuGet/login/issues/6
