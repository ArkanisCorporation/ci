# Composite actions

Shared step bundles used by reusable workflows.

Create `action.yml` in each subdirectory only when implementation starts.
Keep each action narrow; prefer more small actions over one flag-heavy action.

## Available actions

| Action | Purpose | Main side effect |
|---|---|---|
| `dotnet-setversion` | Installs `dotnet-setversion` and applies a bare SemVer value to .NET project files. | Modifies matched `.csproj` files. |
| `setup-dotnet` | Sets up .NET SDK, NuGet cache, and optional local tools. | Writes `NUGET_PACKAGES` and restores cache/tools. |

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
