# ADR-0003: .NET JetBrains CleanupCode Workflow

Status: accepted

## Context

CitizenId verifies JetBrains ReSharper CleanupCode in CI.
Its format job restores the .NET workspace, runs `dotnet jb cleanupcode`, and fails when CleanupCode produces a Git diff.
The local developer path uses Husky.Net tasks that run the same CleanupCode profile before staging changes.
This check is distinct from `dotnet format` because JetBrains CleanupCode applies ReSharper and Rider code style rules.

## Decision

Add `.github/actions/dotnet-jetbrains-cleanupcode/action.yml`.
The composite action runs CleanupCode, captures logs and Git diff diagnostics, and fails when a diff exists.
The composite action delegates native command execution to `run-cleanupcode.cs`, a .NET file script using `CliWrap`.
Add `.github/workflows/wf-setup-dotnet-jetbrains.yml`.
The reusable workflow checks out the caller repository, sets up .NET 10 action tooling, sets up the requested project SDK, restores dependencies, runs the composite action, writes an artifact manifest, and uploads diagnostics.
Default inputs mirror CitizenId: profile `Built-in: Reformat & Apply Syntax Style`, exclude `**/*.razor;**/*.svg;**/*.md`, and pass `--no-updates`.
The default tool mode restores local .NET tools so consumers can pin `JetBrains.ReSharper.GlobalTools` in `.config/dotnet-tools.json`.
An optional installed-tool mode supports repositories that do not yet use local tool manifests.

## Consequences

Consumer repositories can migrate CleanupCode jobs without keeping repo-local shell scripts.
The workflow remains safe for pull requests because it needs only `contents: read`.
CleanupCode may modify files before the diff gate fails, so diagnostics must be uploaded with `if: always()`.
Self-hosted runners must provide Bash, Git, .NET 10 setup support, project SDK setup support, and enough disk for JetBrains caches and generated diagnostics.
Repositories that want exact repeatability should prefer local tool manifests or set `tool-version` when using installed-tool mode.

## Migration

Replace repo-local CleanupCode workflow steps with `wf-setup-dotnet-jetbrains.yml`.
Pass the solution path through `solution`.
Use the default profile and exclude filter for CitizenId-compatible behavior.
Keep `wf-setup-dotnet.yml`, `wf-dotnet-format.yml`, and `wf-dotnet-test.yml` for restore, ordinary format, build, test, and coverage verification.
Run both workflows as separate jobs when both `dotnet format` and JetBrains CleanupCode are required.

## References

- GitHub composite actions: https://docs.github.com/en/actions/tutorials/create-actions/create-a-composite-action
- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- JetBrains ReSharper command line tools: https://www.jetbrains.com/help/resharper/ReSharper_Command_Line_Tools.html
- JetBrains CleanupCode: https://www.jetbrains.com/help/resharper/CleanupCode.html
