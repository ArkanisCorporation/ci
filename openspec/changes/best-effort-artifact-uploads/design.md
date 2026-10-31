# Design

## Context

See proposal.md - Why. Constraints that shape the approach:

- `AGENTS.md` sets an observability floor: every workflow writes a step summary, run metadata,
  an artifact manifest, and uploads diagnostics with `if: always()`. The floor stays; only the
  upload's power to fail the job goes.
- The step summary, run metadata, and manifest files are written before the upload, so the
  summary keeps working when the upload is rejected or switched off.
- `daedalus` runners are containers sharing the host Docker daemon. Job containers and service
  containers are created by the runner on that daemon, but the runner's externals (`/__e/node24`)
  are not mounted into them, so any JavaScript action in a job container fails.
- Public inputs are a compatibility contract (`guidance.md` rule 8): add only in a minor, remove or
  rename only in a major, deprecate with warnings for at least one minor cycle.

## Trust zones

Unchanged. Diagnostics uploads run in whatever zone the workflow already runs in. The required
NuGet package upload stays in the trusted-build to publish handoff of `wf-publish-nuget.yml`.
Hosted vs self-hosted: no new hosted-only or self-hosted-only steps. The sqlx job's bridge-gateway
branch keys on `/.dockerenv`, not on `runs-on-self-hosted`, because what matters is whether the
job shell runs inside a container, which a self-hosted VM runner does not.

## Goals / Non-Goals

**Goals:** a rejected diagnostics upload never fails a job; consumers can stop uploading entirely
without editing workflows; the sqlx check works on hosted runners and on `daedalus`; no caller
breaks.

**Non-Goals:** moving diagnostics to another store, cleaning up existing artifacts, changing the
NuGet package handoff.

## Decisions

### D1. `continue-on-error: true` on the upload step

The upload step keeps `if: always()` semantics and gains `continue-on-error: true`. Its outcome is
`failure` (visible in the log and as an annotation) while its conclusion is `success`, so the job
result reflects the real work. Alternative: `if-no-files-found: ignore` only covers missing files,
not a rejected `CreateArtifact` call. Alternative: dropping uploads entirely loses failure
evidence for repositories that still have quota.

### D2. Classification: required only when another job downloads it

| Workflow | Upload | Class | Why |
|---|---|---|---|
| `wf-publish-nuget.yml` | NuGet package artifact | **required** | `publish-trusted` / `publish-api-key` download it; without it nothing is published. |
| `wf-verify-publish-nuget.yml` | package artifact | best-effort | Nothing downloads it; the pack action already fails when no `.nupkg` is produced, so `if-no-files-found: error` was not the safety net. |
| `wf-deploy-k8s-aspire.yml` | deploy report | best-effort | The deployment has already happened; failing afterwards does not roll it back and only hides a successful deploy. |
| `wf-verify-deploy-k8s-aspire.yml` | dry-run report | best-effort | Diagnostics only. |
| `wf-publish-container-*.yml` | image metadata | best-effort | The digest is a job output, not an artifact; nothing downloads the metadata. |
| `wf-verify-publish-container-*.yml` | image metadata | best-effort | Dry run, diagnostics only. |
| `wf-release-semantic.yml`, `wf-verify-release-semantic.yml` | release diagnostics | best-effort | Release data lives in the GitHub release and tags. |
| all `wf-dotnet-*`, `wf-node-*`, `wf-rust-*`, `wf-setup-dotnet-generated-code.yml`, `wf-publish-docs-openviking.yml` | diagnostics | best-effort | Diagnostics only. |

`docs/artifacts.md` says publish and deploy workflows fail closed when *required* artifacts are
missing; this table is what "required" means.

### D3. `upload-diagnostics` on every workflow with a best-effort upload

Same name, type, and default as the five workflows that already have it. The required NuGet upload
is not gated by it. For the deploy and package workflows the description says what the artifact
is (deploy report, package artifact) rather than inventing a second input name.

### D4. Retention defaults

Artifact storage is billed for the whole retention window, so retention is the lever that matters
for the quota. Pull-request diagnostics are looked at within days, if at all (`docs/artifacts.md`:
"PR diagnostics should be short-lived"). New defaults: 7 days for the workflows that run on every
pull request (`wf-dotnet-format`, `wf-node-*`, `wf-rust-*`, `wf-setup-dotnet-generated-code`,
`wf-verify-release-semantic`, the four `wf-verify-*` dry runs). `wf-dotnet-test` is already 7.
Main-only diagnostics (`wf-release-semantic`, `wf-publish-docs-openviking`) stay at 14, publish and
deploy workflows at 30. This changes no contract: the artifacts it expires are best-effort.

### D5. sqlx job on the runner, Postgres via `docker run`

Replaces ADR-0009's job-container decision (add-rust-workflows D5), which does not work on
`daedalus`. Steps:

1. Determine the database host: `127.0.0.1`, or, when `/.dockerenv` exists, the default bridge
   gateway (`docker network inspect bridge --format '{{(index .IPAM.Config 0).Gateway}}'`). A
   container runner reaches ports the host daemon publishes through that address, as
   Testcontainers does.
2. `docker run -d --rm --name sqlx-<run id>-<attempt>-<random> -p <db host>::5432 <image>`. The
   name is run-scoped plus random and the host port is chosen by Docker, so parallel jobs on one
   daemon never collide. Binding to the database host (not `0.0.0.0`) keeps the throwaway database
   off external interfaces.
3. Read the port with `docker port`, wait with `docker exec <name> pg_isready -h 127.0.0.1` for at
   most 60 seconds. The TCP probe matters: during init the image runs a socket-only server that
   answers on the Unix socket before the real server listens. On timeout print `docker logs` and
   fail.
4. Export `DATABASE_URL` through `GITHUB_ENV` (throwaway credentials, not a secret).
5. An `if: always()` step runs `docker rm -f <name>` when a container was started.

The validate step fails early with a clear message when `docker` is missing or the daemon is
unreachable. `setup-rust-toolchain` already installs rustup on runners without it.

### D6. Deprecate `sqlx-container-image`, do not remove it

Removing an input that callers pass breaks them (`Invalid input` at workflow load). The input stays,
is documented as deprecated and ignored, and the validate step emits `::warning::` when a caller
sets a value other than the old default. Removal is a major-release item.

### D7. Dogfood the sqlx job

`release.yml`'s `rust-check` job sets `sqlx-prepare-check: true` and `sqlx-migrations-source:
migrations`. The mock workspace gains one trivial migration (no sqlx dependency, so the fixture
still compiles in seconds); `sqlx migrate run` proves the database is reachable from the runner and
`cargo sqlx prepare --check` passes with no queries.

## Risks / Trade-offs

- Silent loss of diagnostics: a rejected upload no longer turns the job red. Mitigation: the
  failure stays visible as a step annotation and the step summary is unaffected.
- Bridge gateway assumption: a container runner on a custom network still reaches the bridge
  gateway through the host, which is what MentionBot's local job relies on today. The dogfood job
  proves it on `daedalus`.
