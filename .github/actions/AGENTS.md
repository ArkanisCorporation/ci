# .github/actions/AGENTS.md

## Scope

Applies composite/local actions under `.github/actions/**`.

## Role

Composite actions remove repeated step logic. They do not define job graph, matrix, permissions, environments, concurrency, or secret contract.

Use composite action for:

- Tool setup wrapper.
- Metadata generation.
- Manifest creation.
- Summary publishing.
- Diagnostics collection.
- Change detection.

Use reusable workflow instead for:

- Multiple jobs.
- Matrix strategy.
- Runner selection.
- Permissions.
- Environment protection.
- Secret/OIDC lifecycle.
- Publish/deploy orchestration.

## Action rules

- Each action has `action.yml`.
- Declare all inputs/outputs.
- Set `shell` explicitly for every `run` step.
- Use cross-platform shell only when tested; otherwise document OS constraint.
- Do not assume checkout already happened unless documented.
- No secret outputs.
- No writes outside workspace/temp/cache dirs.
- No network calls unless action purpose requires it and docs say so.
- No parallel/background step syntax inside composite actions.
- Fail with actionable error messages.
- Mask derived sensitive values.
- Keep outputs small; write large data to files/artifacts.

## Versioning

Composite action input/output contract follows same compatibility policy as workflows.

- Additive input/output -> minor.
- Rename/remove/semantic change -> major.
- Bug fix preserving contract -> patch.

## Test floor

Each action needs fixture usage in `tests/fixtures/action-contract/` or platform selftest.

Validate:

- Required input missing -> clear failure.
- Happy path -> expected outputs.
- Failure path -> diagnostics preserved.
- Shell quoting safe for spaces/special chars.
