# References

Last verified: 2026-07-09.

## Primary Docs

- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub reusable workflow configuration comparison: https://docs.github.com/en/actions/concepts/workflows-and-actions/reusing-workflow-configurations
- GitHub workflow syntax: https://docs.github.com/actions/using-workflows/workflow-syntax-for-github-actions
- GitHub workflow concurrency: https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency
- GitHub contexts reference: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- GitHub variables reference: https://docs.github.com/en/actions/reference/workflows-and-actions/variables
- GitHub GITHUB_TOKEN permissions: https://docs.github.com/actions/reference/authentication-in-a-workflow
- GitHub Packages with Actions: https://docs.github.com/en/packages/managing-github-packages-using-github-actions-workflows/publishing-and-installing-a-package-with-github-actions
- GitHub secrets in conditionals: https://docs.github.com/actions/security-guides/using-secrets-in-github-actions
- GitHub OIDC with reusable workflows: https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-with-reusable-workflows
- GitHub composite action metadata: https://docs.github.com/en/actions/reference/workflows-and-actions/metadata-syntax
- GitHub composite action tutorial: https://docs.github.com/en/actions/tutorials/create-actions/create-a-composite-action
- GitHub secure use reference: https://docs.github.com/en/actions/reference/security/secure-use
- GitHub artifacts: https://docs.github.com/en/actions/tutorials/store-and-share-data
- GitHub artifact retention and removal: https://docs.github.com/actions/managing-workflow-runs/removing-workflow-artifacts
- GitHub debug logging: https://docs.github.com/actions/managing-workflow-runs/enabling-debug-logging
- actions/upload-artifact path patterns: https://github.com/actions/upload-artifact
- GitHub dependency caching: https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching
- GitHub-hosted runners: https://docs.github.com/en/actions/reference/runners/github-hosted-runners
- GitHub self-hosted runners: https://docs.github.com/en/actions/reference/runners/self-hosted-runners
- actions/setup-node: https://github.com/actions/setup-node
- Docker build-push action: https://github.com/docker/build-push-action
- Docker Buildx secrets in GitHub Actions: https://docs.docker.com/build/ci/github-actions/secrets/
- Docker login action: https://github.com/docker/login-action
- NuGet authenticated feeds: https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds
- NuGet config reference: https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file
- dotnet tool install: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install
- dotnet tool restore: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-restore
- dotnet-setversion NuGet package: https://www.nuget.org/packages/dotnet-setversion
- JetBrains ReSharper command line tools: https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html
- JetBrains CleanupCode: https://www.jetbrains.com/help/resharper/CleanupCode.html
- JetBrains InspectCode: https://www.jetbrains.com/help/resharper/InspectCode.html
- ReportGenerator: https://github.com/danielpalme/ReportGenerator
- GitHub CLI pull request commands: https://cli.github.com/manual/gh_pr
- semantic-release configuration: https://semantic-release.gitbook.io/semantic-release/usage/configuration
- semantic-release plugins: https://semantic-release.gitbook.io/semantic-release/extending/plugins-list
- semantic-release dry-run and Git authorization behavior: https://github.com/semantic-release/semantic-release/blob/master/lib/git.js
- semantic-release GitHub plugin: https://github.com/semantic-release/github
- cycjimmy semantic-release-action inputs: https://github.com/cycjimmy/semantic-release-action
- semantic-release-major-tag plugin: https://github.com/doteric/semantic-release-major-tag
- Node.js Corepack: https://nodejs.org/api/corepack.html
- GitHub local reusable workflow reference changelog: https://github.blog/changelog/2022-01-25-github-actions-reusable-workflows-can-be-referenced-locally/
- NuGet Trusted Publishing: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
- NuGet/login action: https://github.com/NuGet/login
- actions/setup-dotnet: https://github.com/actions/setup-dotnet
- 1Password GitHub Actions integration: https://www.1password.dev/ci-cd/github-actions
- 1Password load-secrets-action: https://github.com/1Password/load-secrets-action
- nektos/act: https://github.com/nektos/act
- actionlint: https://github.com/rhysd/actionlint
- raven-actions/actionlint: https://github.com/raven-actions/actionlint

## Current Platform Changes To Monitor

- Node20 to Node24 runner runtime migration: https://github.blog/changelog/2025-09-19-deprecation-of-node-20-on-github-actions-runners/
- Parallel and background steps: https://github.blog/changelog/2026-06-25-actions-steps-can-now-be-run-in-parallel/
- Read-only cache for untrusted triggers: https://github.blog/changelog/2026-06-26-read-only-actions-cache-for-untrusted-triggers/
- Non-zipped artifacts: https://github.blog/changelog/2026-02-26-github-actions-now-supports-uploading-and-downloading-non-zipped-artifacts/
- Self-hosted runner minimum version enforcement: https://github.blog/changelog/2026-06-12-github-actions-minimum-version-enforcement-timeline-for-self-hosted-runners/
- Safer `pull_request_target` defaults: https://github.blog/changelog/2026-06-18-safer-pull_request_target-defaults-for-github-actions-checkout/
- GitHub Actions larger concurrency queues: https://github.blog/changelog/2026-05-07-github-actions-concurrency-groups-now-allow-larger-queues/

## Known Edge Discussions

- Composite actions inside external reusable workflows need explicit repository references or checked-out platform code: https://github.com/orgs/community/discussions/18601
- Local composite actions resolve against the checked-out caller workspace: https://github.com/actions/runner/issues/1348
- NuGet Trusted Publishing with shared workflows can require careful policy ownership: https://github.com/NuGet/login/issues/6
- NuGet Trusted Publishing and shared workflow discussion: https://github.com/orgs/community/discussions/179952
- actions/setup-dotnet multiple source request: https://github.com/actions/setup-dotnet/issues/167
- semantic-release major tag update request: https://github.com/semantic-release/semantic-release/issues/1515
- semantic-release dry-run still checks push permissions: https://github.com/semantic-release/semantic-release/issues/2232
- actionlint `queue` key support gap: https://github.com/rhysd/actionlint/issues/657
