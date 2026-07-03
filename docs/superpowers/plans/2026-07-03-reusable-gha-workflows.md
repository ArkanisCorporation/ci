# Reusable GitHub Actions Workflows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.
> Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the first .NET-centered reusable GitHub Actions workflow slice into the `ci` repository.

**Architecture:** Public reusable workflows live directly under `.github/workflows`.
Composite actions live under `.github/actions` and only bundle repeated steps.
Consumer repositories keep small wrapper workflows that call the platform workflows by pinned ref.

**Tech Stack:** GitHub Actions reusable workflows, composite actions, .NET SDK, Docker Buildx, semantic-release, NuGet Trusted Publishing, nektos/act, actionlint.

---

### Task 1: Validation Harness

**Files:**
- Modify: `scripts/validate-workflows.cs`
- Create: `.github/workflows/wf-platform-selftest.yml`
- Create: `tests/fixtures/workflow-contract/dotnet-consumer.yml`
- Create: `tests/fixtures/events/pull_request.json`

- [ ] Add checks that workflow files parse, public workflow files use `workflow_call`, top-level workflow permissions are explicit, and external actions use version tags.
- [ ] Add fixture caller workflow for local `act` smoke validation.
- [ ] Run validation before public workflows exist to confirm missing workflow checks fail.

### Task 2: Shared Composite Actions

**Files:**
- Create: `.github/actions/setup-dotnet/action.yml`
- Create: `.github/actions/write-run-metadata/action.yml`
- Create: `.github/actions/write-artifact-manifest/action.yml`
- Create: `.github/actions/publish-summary/action.yml`

- [ ] Implement small actions for setup, metadata, manifest, and summary output.
- [ ] Document inputs, outputs, preconditions, side effects, and failure behavior in action metadata.
- [ ] Keep action code step-driven and avoid repository-specific shell scripts.

### Task 3: Public Workflows

**Files:**
- Create: `.github/workflows/wf-setup-dotnet.yml`
- Create: `.github/workflows/wf-release-semantic.yml`
- Create: `.github/workflows/wf-publish-nuget.yml`
- Create: `.github/workflows/wf-publish-container.yml`
- Create: `.github/workflows/wf-deploy-k8s-aspire.yml`

- [ ] Implement .NET restore/build/test/coverage workflow under the requested `wf-setup-*` naming.
- [ ] Implement semantic-release workflow without the `@semantic-release/exec` plugin and with a guard that fails when exec plugin usage is detected unless explicitly allowed.
- [ ] Implement NuGet publish as a step-driven package push workflow with Trusted Publishing support and API-key fallback.
- [ ] Implement Docker Buildx publish workflow with digest and metadata outputs.
- [ ] Implement Aspire Kubernetes deploy workflow with namespace validation and explicit runner/tool preconditions.

### Task 4: Docs, Schemas, AGENTS Compression

**Files:**
- Modify: `AGENTS.md`
- Modify: `.github/workflows/AGENTS.md`
- Modify: `README.md`
- Modify: `docs/workflow-catalog.md`
- Modify: `docs/security-model.md`
- Modify: `docs/observability.md`
- Modify: `docs/artifacts.md`
- Modify: `docs/runner-contract.md`
- Modify: `docs/references.md`
- Modify or create: `schemas/workflow-inputs/*.schema.json`

- [ ] Compress noisy agent instructions while preserving high-signal rules.
- [ ] Document each workflow contract, inputs, secrets, outputs, permissions, artifacts, preconditions, and side effects.
- [ ] Update schemas to match workflow inputs.
- [ ] Capture the reusable workflow and NuGet Trusted Publishing edge cases.

### Task 5: Verification

**Files:**
- No implementation files.

- [ ] Run `dotnet run --file scripts/validate-workflows.cs`.
- [ ] Run `act --list` against the platform self-test workflow.
- [ ] Run `act` for a bounded fixture job when the local Docker/act environment supports it.
