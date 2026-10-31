# Proposal

## Why

The organisation's GitHub Actions artifact storage quota is exhausted ("Failed to CreateArtifact:
Artifact storage quota has been hit"). Raising the quota, deleting artifacts, or making
repositories public are not options. Every public workflow uploads diagnostics or metadata with
`if: always()` and no `continue-on-error`, so a rejected upload fails the whole job even when the
real work passed. Every consumer (MentionBot, PatchWatch, Infrastructure) is red for a reason that
says nothing about their code. An upload of diagnostics must never decide whether a build passes.

Separately, the `sqlx` job of `wf-rust-check.yml` runs in a job `container:` with a Postgres
`services:` entry. On `daedalus` the runner is itself a container on the host Docker daemon, and job
containers fail at checkout (`exec: "/__e/node24/bin/node": no such file or directory`). The
dogfood job never enabled `sqlx-prepare-check`, so this was not caught before release (ARK-687).

## What Changes

- **Best-effort diagnostics uploads.** Every `actions/upload-artifact` step that carries
  diagnostics, metadata, or reports nobody downloads gets `continue-on-error: true`. A failed
  upload shows as a warning on the step; the job result is decided by the real work.
- **`upload-diagnostics` everywhere.** Every public workflow with a best-effort upload exposes
  `upload-diagnostics` (boolean, default `true`, same as the Node, Rust, and OpenViking workflows
  already do) and gates the upload on it, so a consumer can switch uploads off entirely while the
  quota is exhausted.
- **Required uploads stay required.** The one upload another job downloads, the NuGet package
  artifact of `wf-publish-nuget.yml` (consumed by the publish jobs), keeps failing the job.
- **Shorter default retention for pull-request diagnostics.** Defaults drop to 7 days (from 14 or
  30) for workflows that run on every pull request: lint, test, check, generated-code, and the
  dry-run verify workflows. Main-only diagnostics (`wf-release-semantic`, `wf-publish-docs-openviking`)
  keep 14 days, publish and deploy workflows keep 30. Callers that set `artifact-retention-days`
  are unaffected.
- **Container-free sqlx check.** The `sqlx` job runs on the runner: it starts Postgres with
  `docker run` on a run-scoped container name and a random host port, waits over TCP with a
  bounded loop, reaches it through the Docker bridge gateway when the runner is itself a container
  (`/.dockerenv`), and removes the container in an `always()` step.
- **`sqlx-container-image` deprecated.** The input stays accepted (so callers that pass it keep
  working) but is ignored, and a non-default value emits a deprecation warning. Removal waits for
  the next major.
- **Dogfood covers sqlx.** The `rust-check` job in `release.yml` enables `sqlx-prepare-check` with a
  fixture migration, so the Postgres path runs on `daedalus` in every pull request.
- **Validator** enforces both rules: every upload step is best-effort and gated unless listed as
  required, and the sqlx job uses neither `container:` nor `services:`.

Out of scope: changing where diagnostics live (e.g. step summaries only), artifact cleanup
automation, and the quota itself. MentionBot drops its local `sqlx` job after release (follow-up
in ARK-693).

## Capabilities

### New Capabilities

- `artifact-uploads`: which reusable-workflow uploads are best-effort, which are required, and how
  callers control them.

### Modified Capabilities

- `rust-verification`: the opt-in sqlx check no longer needs job or service containers.

## Impact

- Additive input (`upload-diagnostics`) on 12 workflows; default behaviour still uploads.
- Behaviour fix: jobs no longer fail on a rejected diagnostics upload. Ships as `fix:` on `v1`.
- Default `artifact-retention-days` lower on verification workflows (not a contract change: the
  value only controls expiry of best-effort diagnostics).
- `sqlx-prepare-check` callers now need Docker on the runner instead of job-container support;
  both hosted `ubuntu-latest` and `daedalus` provide it.
