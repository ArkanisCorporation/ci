# tests/AGENTS.md

## Scope

Applies tests under `tests/**`.

## Goal

Tests prove workflow/action contract, not every upstream tool behavior.

## Required test classes

- Schema validation.
- Workflow syntax/static policy.
- Composite action contract.
- Fixture consumer workflows.
- Security pattern scan.
- Artifact manifest validation.
- Snapshot docs generation.

## Rules

- Add fixture for every public workflow.
- Add negative test for dangerous event/permission pattern.
- Test self-host label input parsing via JSON.
- Test missing artifacts fail for release flows.
- Keep snapshots stable; update only with intentional doc/API change.
- Do not require real cloud/K8s/registry creds in default test suite.
