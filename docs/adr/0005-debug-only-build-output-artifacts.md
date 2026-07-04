# ADR-0005: Debug-only build output artifacts

Status: accepted

## Context

Diagnostic artifacts are meant to explain CI failures without becoming build-output archives.
.NET runs can create large `bin/` and `obj/` trees under broad artifact or generated-code paths.
Those paths are useful during rare deep debugging, but they make normal artifacts expensive to upload, download, and inspect.
GitHub Actions exposes `runner.debug` when debug logging is enabled, and `actions/upload-artifact` supports exclusion patterns in the `path` input.
Last verified: 2026-07-04.

## Decision

Exclude `artifacts/**/bin/**` and `artifacts/**/obj/**` from whole-tree diagnostic uploads unless `runner.debug == '1'`.
Ignore `bin/` and `obj/` Git pathspecs in `dotnet-generated-code-diff` unless `runner.debug` is enabled.
Do not add a new public workflow input.
Use the existing GitHub Actions debug flag as the escape hatch.

## Consequences

Normal diagnostic artifacts stay smaller and focused on logs, manifests, test results, coverage, and metadata.
Debug reruns can still capture build outputs when a maintainer needs them.
Callers do not need to change workflow inputs.
Generated-code checks no longer fail or bloat diagnostics because a broad generated path contains build output.

## Migration

No caller migration is required.
Re-run a workflow with GitHub Actions debug logging when `bin/` or `obj/` contents are needed for investigation.

## References

- GitHub Actions debug logging: https://docs.github.com/actions/managing-workflow-runs/enabling-debug-logging
- GitHub Actions contexts reference: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- actions/upload-artifact path patterns: https://github.com/actions/upload-artifact
