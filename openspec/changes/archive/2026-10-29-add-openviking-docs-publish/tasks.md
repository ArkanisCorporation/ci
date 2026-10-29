# Tasks

## 1. Workflow

- [x] 1.1 Add `.github/workflows/wf-publish-docs-openviking.yml` with the inputs and outputs from the design table, top-level `permissions: {}`, job `contents: read`, the runner collapse expression, the D8 concurrency group, a job display name (`docs @ ${{ github.head_ref || github.ref_name }}`), and a header comment covering purpose, preconditions (runner env from ARK-652), side effects, and trust zone; verify with actionlint 1.7.12
- [x] 1.2 Implement the steps in the shape order from `.github/workflows/AGENTS.md`: checkout caller (`persist-credentials: false`), runner contract record, self-hosted preflight (disk, `git`, `curl`, `jq`; fail closed when missing), publishing gate (D2), credential check plus `::add-mask::` (D1, skipped on dry run), collect (`git ls-files -z` with `:(glob)` pathspecs from `include-paths`) and archive (`git archive` from the exact list, D3), empty-set skip (D6), size cap (D6), then `temp_upload` and import with `to`, tags, `processing_mode`, `wait`, and bounded timeouts (D7, key via `curl -K -`); verify each failure path prints a reason without the key
- [x] 1.3 Write the step summary, `run-metadata.json` (conforming to `schemas/run-metadata.schema.json`), and outputs; upload diagnostics (file list, archive size, redacted HTTP responses) with `if: always()`; verify a dry run's summary and artifact show the file list and `published=false`

## 2. Contract files

- [x] 2.1 Add `schemas/workflow-inputs/wf-publish-docs-openviking.schema.json` matching every input, type, and default; verify `tests/validate-workflow-input-schema-parity.ps1` and the validator's schema checks pass
- [x] 2.2 Add `tests/fixtures/workflow-contract/openviking-docs-consumer.yml`, a caller identical in shape to repo-standards' template; verify fixture validation passes
- [x] 2.3 Extend `scripts/validate-workflows.cs`: job and step display-name entries, the concurrency contract entry, and a check that the `include-paths` default equals the seven standard globs (the cross-repo contract); verify `dotnet run --file scripts/validate-workflows.cs` passes and fails when the default is edited

## 3. Dogfood

- [x] 3.1 Add a `release.yml` job calling `./.github/workflows/wf-publish-docs-openviking.yml` with `dry-run: true` on the default runner, wired into `verify-release`'s `needs` like the other fixture jobs; verify it passes on this PR and lists this repo's docs
- [x] 3.2 Add `.github/workflows/openviking-docs.yml` from repo-standards' `templates/openviking-docs.yml` (calling `@v1`), differing only where D10 says; verify actionlint and the validator pass

## 4. Documentation

- [x] 4.1 README *Public Workflows* row, `docs/workflow-catalog.md` section (purpose, trust zone, permissions, inputs, outputs, diagram, caller example linking the repo-standards template), and the workflows `AGENTS.md` list; verify `ValidateWorkflowCatalogDiagrams` and any generated-docs snapshot checks pass
- [x] 4.2 Add `docs/adr/0008-openviking-docs-publish.md` (reachability, runner-provided credential, default-branch gate, layout, deletion behaviour); verify it links ARK-642, ARK-651, ARK-652

## 5. Integration

- [x] 5.1 Run the full local validation (`dotnet run --file scripts/validate-workflows.cs` with actionlint 1.7.12 on PATH) and verify it passes; mutation-check the new contract (edited `include-paths` default and removed mask both fail, the schema parity check fires too). `tests/validate-workflow-input-schema-parity.ps1` was not run locally (no `pwsh`); it only mutation-tests `wf-node-lint`
- [x] 5.2 Run `openspec validate add-openviking-docs-publish --strict` and verify it passes
- [x] 5.3 Retitle the PR with a `feat:` type, so the squash merge releases the workflow (the superseded superpowers plan was already removed with this proposal)

## 6. Release (human)

- [x] 6.1 Merge; confirm semantic-release moved `v1` and the workflow exists at `ArkanisCorporation/ci@v1`

## 7. Live verification (before repo-standards#25 merges)

- [x] 7.1 Dispatch `openviking-docs` in `ci`; verify the run succeeds on `arkanis-runners`, the `git`/`curl`/`jq` preflight passes, and the summary shows the file count and target URI
- [x] 7.2 Verify the stored layout: `viking://resources/arkanis/projects/ci/README.md` and `.../ci/docs/adr/` exist (no extra archive-root directory), tags `repo=ci` and `commit=<sha>` are present, and a search scoped to the target returns them (layout and search verified; each `.md` becomes a directory of sections; tags are accepted but no user tool shows them, see ADR-0008)
- [x] 7.3 Verify deletions: land a `docs:` commit that deletes or renames an obsolete doc, let the caller run, and confirm the old path is gone from the mirror; if it remains, implement D5's prune step (and request delete rights for the `ci` user) before continuing (it was removed; no prune step needed)
- [x] 7.4 Record the observed deletion behaviour and OV version in ADR-0008, then hand off to merging repo-standards#25

## Workflow follow-up

- Archive this change after merge, so the spec lands in `openspec/specs/openviking-docs-publish/`.
