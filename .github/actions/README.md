# Composite actions

Shared step bundles used by reusable workflows.

Create `action.yml` in each subdirectory only when implementation starts.
Keep each action narrow; prefer more small actions over one flag-heavy action.

## Available actions

| Action | Purpose | Main side effect |
|---|---|---|
| `dotnet-jetbrains-cleanupcode` | Runs JetBrains ReSharper CleanupCode and verifies that it produces no Git diff. | May modify workspace files before failing on diff. |
| `dotnet-setversion` | Installs `dotnet-setversion` and applies a bare SemVer value to .NET project files. | Modifies matched `.csproj` files. |
| `setup-dotnet` | Sets up .NET SDK, NuGet cache, and optional local tools. | Writes `NUGET_PACKAGES` and restores cache/tools. |

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
