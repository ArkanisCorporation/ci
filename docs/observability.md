# Observability

Audience: consumers and maintainers.

## Required Outputs

Every public workflow writes a step summary.
Every public workflow records whether it targets self-hosted runners and the requested runner labels.
Workflows that produce files write an artifact manifest.
Workflows that perform verification or release work upload diagnostics with `if: always()`.

## Step Timeline Names

Step names should include the highest-signal dynamic value available in scope.
Prefer outputs from previous steps when they exist, then fall back to workflow inputs or GitHub context values.
Use the shape `Action detail @ value` for ordinary steps.
When a step name mentions dry-run behavior, use `Action (Dry Run) @ value`.
Do not include secrets, kubeconfig content, registry tokens, OIDC tokens, raw build arguments, multiline plugin lists, or long multiline tag lists in step names.

## Summary Fields

Summaries should include:

- workflow name;
- runner kind;
- requested runner labels;
- repository;
- SHA;
- key inputs;
- artifact manifest path;
- output digest or version when available;
- failure hints when a step can produce them.

## Runner Metadata

Self-hosted runs should write a preflight file under `artifacts/meta/`.
The preflight file should include disk space, workspace, runner OS, runner arch, and required tool paths.

Hosted runs should still record the effective runner labels resolved from `runs-on` or `runs-on-json`.

## Diagnostic Layout

Use bounded folders:

```text
artifacts/
  meta/
  msbuild/
  test-results/
  coverage/
  release/
  nuget/
  container/
  k8s/
```

## Debug Rules

Debug output may include resolved input values and tool versions.
Debug output must not include secrets, kubeconfig content, registry tokens, OIDC tokens, full environment dumps, or credential files.
