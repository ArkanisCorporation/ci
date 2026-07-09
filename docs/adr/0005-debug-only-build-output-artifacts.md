# ADR-0005: Debug-only build output artifacts

Status: accepted

## Context

Diagnostic artifacts are meant to explain CI failures without becoming build-output archives.
.NET runs can create large `bin/` and `obj/` trees under broad artifact or generated-code paths.
Those paths are useful during rare deep debugging, but they make normal artifacts expensive to upload, download, and inspect.
Test result and coverage report directories can also dominate normal diagnostic artifacts while the step summary and pull request comment carry the usual coverage signal.
GitHub Actions exposes `runner.debug` when debug logging is enabled, and `actions/upload-artifact` supports exclusion patterns in the `path` input.
Last verified: 2026-07-09.

## Decision

Exclude `artifacts/**/bin/**` and `artifacts/**/obj/**` from whole-tree diagnostic uploads unless `runner.debug == '1'`.
Exclude `artifacts/test-results/**` and `artifacts/coverage/**` from `wf-dotnet-test.yml` diagnostic uploads unless `runner.debug == '1'`.
Allow callers to opt those two directories back in independently with `upload-test-results` and `upload-coverage`.
Ignore `bin/` and `obj/` Git pathspecs in `dotnet-generated-code-diff` unless `runner.debug` is enabled.
Use the existing GitHub Actions debug flag as the escape hatch.

## Consequences

Normal diagnostic artifacts stay smaller and focused on logs, manifests, binlogs, summaries, and metadata.
Debug reruns can still capture build outputs, test results, and coverage output when a maintainer needs them.
Callers do not need to change workflow inputs unless they intentionally want uploaded test results or coverage output.
Generated-code checks no longer fail or bloat diagnostics because a broad generated path contains build output.

## Migration

No caller migration is required.
Set `upload-test-results` or `upload-coverage` for routine artifact capture.
Re-run a workflow with GitHub Actions debug logging when `bin/`, `obj/`, test result, or coverage contents are needed for broader investigation.

## References

- GitHub Actions debug logging: https://docs.github.com/actions/managing-workflow-runs/enabling-debug-logging
- GitHub Actions contexts reference: https://docs.github.com/en/actions/reference/workflows-and-actions/contexts
- actions/upload-artifact path patterns: https://github.com/actions/upload-artifact
