# Composite actions

Shared step bundles used by reusable workflows.

Create `action.yml` in each subdirectory only when implementation starts.
Keep each action narrow; prefer more small actions over one flag-heavy action.

## Available actions

| Action | Purpose | Main side effect |
|---|---|---|
| `dotnet-coverage-report` | Generates ReportGenerator coverage output and optional PR comments. | Writes coverage report files and may update one PR comment. |
| `dotnet-generated-code-diff` | Runs generated-code commands and verifies generated paths stay unchanged. | May modify workspace files before failing on diff. |
| `dotnet-jetbrains-cleanupcode` | Runs JetBrains ReSharper CleanupCode and verifies that it produces no Git diff. | May modify workspace files before failing on diff. |
| `dotnet-setversion` | Installs `dotnet-setversion` and applies a bare SemVer value to .NET project files. | Modifies matched `.csproj` files. |
| `release-backpropagation` | Creates, approves, and optionally auto-merges release backpropagation PRs. | May create PRs, approve them, and enable auto-merge. |
| `setup-dotnet` | Sets up .NET SDK, NuGet cache, and optional local tools. | Writes `NUGET_PACKAGES` and restores cache/tools. |

## dotnet-coverage-report

Use `dotnet-coverage-report` after `dotnet test --collect:"XPlat Code Coverage"`.
The action installs `dotnet-reportgenerator-globaltool`, generates reports, appends `SummaryGithub.md` to the step summary, and optionally updates one pull request comment.

Preconditions:

- The runner has Bash and .NET 10 SDK.
- ReportGenerator is installed without .NET tool roll-forward, so the action tooling runtime must support the selected ReportGenerator tool version.
- Coverage files exist at the `reports` glob unless `fail-if-no-reports` is false.

Requirements:

| Requirement | Permission | Mode |
|---|---|---|
| GitHub CLI, `GH_TOKEN`, and `pr-number` for pull request comments. | `pull-requests: write` | `comment-on-pr` |
| Coverage files at the `reports` glob. | none | report generation |

Side effects:

- Installs ReportGenerator under `$RUNNER_TEMP/arkanis-reportgenerator`.
- Writes report files under `targetdir`.
- May create or update one pull request comment when `comment-on-pr` is true.

Example:

```yaml
steps:
  - name: Generate coverage report
    uses: ArkanisCorporation/ci/.github/actions/dotnet-coverage-report@v1
    env:
      GH_TOKEN: ${{ github.token }}
    with:
      reports: artifacts/test-results/**/coverage.cobertura.xml
      targetdir: artifacts/coverage/report
      comment-on-pr: true
      pr-number: ${{ github.event.number }}
```

## dotnet-generated-code-diff

Use `dotnet-generated-code-diff` when a repository commits generated .NET source and CI must fail if generator output is stale.
The action runs newline-delimited commands, then checks generated paths for tracked diffs and untracked files.
CitizenId uses this shape for Wolverine generated handlers.

Preconditions:

- The runner has Bash, Git, and .NET 10 SDK.
- The repository has already been checked out.
- Required restore or build steps have already run unless commands perform them.
- Generated paths are repository-relative paths.

Side effects:

- Runs commands that may modify workspace files.
- Writes command logs, changed-file lists, diff stats, and diff previews under `artifacts/generated-code`.
- Writes a short generated-code summary to `$GITHUB_STEP_SUMMARY`.

Example:

```yaml
steps:
  - name: Verify generated handlers
    uses: ArkanisCorporation/ci/.github/actions/dotnet-generated-code-diff@v1
    with:
      commands: |
        dotnet run --project src/App.Web/App.Web.csproj --no-build -- codegen write
      generated-paths: |
        src/App.Web/Internal/Generated
```

## dotnet-jetbrains-cleanupcode

Use `dotnet-jetbrains-cleanupcode` when a .NET repository treats JetBrains CleanupCode as a formatting gate.
The default command mirrors CitizenId CI: `dotnet jb cleanupcode <solution> --profile="Built-in: Reformat & Apply Syntax Style" --exclude="**/*.razor;**/*.svg;**/*.md" --no-updates`.
The action fails when CleanupCode creates a Git diff.

Preconditions:

- The runner has Bash, Git, and .NET 10 SDK.
- The repository has already been checked out.
- By default, `.config/dotnet-tools.json` contains `JetBrains.ReSharper.GlobalTools`.
- Set `install-tool: true` and `tool-version` when the caller wants this action to install JetBrains tools instead of using local tools.

Side effects:

- Runs the action implementation as a .NET file script with `CliWrap`.
- Runs `dotnet tool restore` by default.
- Runs CleanupCode, which can modify workspace files.
- Writes logs, changed-file lists, diff stats, and diff previews under `artifacts/jetbrains-cleanupcode`.
- Writes a short cleanup summary to `$GITHUB_STEP_SUMMARY`.

Example:

```yaml
steps:
  - name: Checkout
    uses: actions/checkout@v7

  - name: Verify JetBrains CleanupCode
    uses: ArkanisCorporation/ci/.github/actions/dotnet-jetbrains-cleanupcode@v1
    with:
      solution: CitizenId.slnx
      profile: "Built-in: Reformat & Apply Syntax Style"
      exclude: "**/*.razor;**/*.svg;**/*.md"
```

## release-backpropagation

Use `release-backpropagation` from trusted release jobs that need to merge a release branch back into the default branch.
The action creates a pull request or reuses an existing one from the release branch.
It can approve the PR with `PR_AUTOMATION_PAT` and enable auto-merge with `GH_TOKEN`.

Preconditions:

- The runner has GitHub CLI and .NET 10 SDK.
- `GH_TOKEN` can create PRs and enable auto-merge.
- `PR_AUTOMATION_PAT` can approve the PR when `approve` is true.

Side effects:

- May create a pull request.
- May approve a pull request with the automation token.
- May enable auto-merge.

Example:

```yaml
steps:
  - name: Backpropagate release branch
    uses: ArkanisCorporation/ci/.github/actions/release-backpropagation@v1
    env:
      GH_TOKEN: ${{ github.token }}
      PR_AUTOMATION_PAT: ${{ secrets.PR_AUTOMATION_PAT }}
    with:
      new-version: 1.2.3
      release-ref-name: release/stable
      default-branch: main
```

## dotnet-setversion

Use `dotnet-setversion` before packaging or container publishing when runtime assemblies must reflect the semantic-release version.
Pass `version` without a leading `v`.
Use Docker or Kubernetes tags through separate workflow inputs such as `version-tag`.

Preconditions:

- The runner has Bash.
- The runner can install the requested .NET SDK through `actions/setup-dotnet`.
- Network access to NuGet is available for `dotnet tool install dotnet-setversion`.
- `working-directory` or `project` points at the caller repository checkout.

Side effects:

- Installs `dotnet-setversion` under `$RUNNER_TEMP/arkanis-dotnet-setversion`.
- Modifies matched `.csproj` files in the workspace.
- Writes a short stamp summary to `$GITHUB_STEP_SUMMARY`.

Example:

```yaml
steps:
  - name: Checkout
    uses: actions/checkout@v7

  - name: Stamp project version
    uses: ArkanisCorporation/ci/.github/actions/dotnet-setversion@v1
    with:
      version: 1.2.3
      working-directory: .
      recursive: true
```
