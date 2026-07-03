# Reusable workflow directory

Place public workflow API files here. GitHub requires reusable workflow files directly under `.github/workflows`.

Do not add subdirectories for workflows. Use filename prefixes instead.

Start each workflow from `AGENTS.md` contract, then add schema in `schemas/workflow-inputs/`, docs in `docs/workflow-catalog.md`, fixture in `tests/fixtures/`.

## Repository workflows

`build.yml` is this repository's pull request and main push self-test pipeline.
It calls `wf-platform-selftest.yml` through a local reusable workflow reference.

`release.yml` is this repository's release pipeline.
It runs `wf-platform-selftest.yml` before `wf-release-semantic.yml`.
It publishes GitHub release metadata only.
