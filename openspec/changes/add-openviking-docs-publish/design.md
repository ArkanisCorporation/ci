# Design

## Context

- **Reachability and identity are solved outside this repo.** OV's API is a ClusterIP service
  (`http://openviking.openviking.svc.cluster.local:1933`). A NetworkPolicy admits only
  `arkanis-runners` pods (ARK-651). Jobs authenticate as the non-admin OV user `ci`, scoped to
  `add_resource` under `viking://resources/`. The pool injects `OPENVIKING_URL` and
  `OPENVIKING_API_KEY` as plain container env (ARK-652; the secret ref is `optional: true`). There is
  no GitHub secret, because org Actions secrets are unavailable to private repos on GitHub Free.
- **OV import API (raw HTTP):** a local directory must be zipped and uploaded to
  `POST /api/v1/resources/temp_upload` (multipart `file`), and the returned `temp_file_id` is passed
  to `POST /api/v1/resources`. Per the OV docs, `to` is the final location: "If the target already
  exists, it is refreshed... files absent from the new import may be removed; unchanged content is
  reused". Tags are per request (`tags`, `tag_mode: replace`). `wait` makes the call block until
  processing finishes; without it, the call returns after parsing and queues the processing. The deployed server (v0.5.2) rejects unknown body fields and has no
  `timeout` field, so the client bounds the wait. Auth header: `X-API-Key`.
- **Consumers:** callers come from repo-standards' template (`templates/openviking-docs.yml`), which
  calls `@v1` with `runs-on: arkanis-runners` and `runs-on-self-hosted: true`. It triggers on push to
  the default branch (with a `paths:` filter for the doc globs and the caller itself) and on
  `workflow_dispatch`.
- **Repo conventions** (`AGENTS.md`, `.github/workflows/AGENTS.md`, `scripts/validate-workflows.cs`):
  runner inputs and the collapse expression, `persist-credentials: false` checkout, display-name
  contracts, schema parity, catalog diagram per workflow, observability floor, and a concurrency
  contract for state-changing workflows.

## Goals / Non-Goals

**Goals:**

- A caller that passes no inputs works on `arkanis-runners` and publishes the standard doc set.
- Deterministic, tool-light packaging: only tracked files, at the checked-out commit.
- No credential exposure, even though the key is plain runner env rather than a GitHub secret.
- Safe to rerun: each run converges the mirror on the current commit.

**Non-Goals:**

- Adoption measurement, templates, or PRs (repo-standards).
- Non-Markdown content or OV watches.
- A hosted-runner path that reaches OV. Hosted runners can only dry-run.

## Decisions

### D1. Trust zone: publish, with a runner-provided credential

The workflow writes to an external store from a protected default-branch push, which is the
*publish* zone. Unlike the other publish workflows, the credential is not OIDC or a package secret.
It is an environment variable on one runner pool. That is ARK-652's accepted trade-off: every job on
`arkanis-runners` can read the key, which is well below the `arc-deployer` service account those
jobs already hold. The OV key is non-admin, and the NetworkPolicy limits reachability to that pool.

**Consequence:** GitHub does not auto-mask the key, because it is not a secret. The first step that
touches it runs `echo "::add-mask::${OPENVIKING_API_KEY}"`, and the key is handed to `curl` through
a config file on stdin (`curl -K -`), never on the argv where `/proc` or `set -x` would show it.

### D2. Publishing gate: default branch only, no pull-request events

Unless `dry-run` is set, the job fails unless `github.event_name` is not `pull_request` /
`pull_request_target` and `github.ref == format('refs/heads/{0}', github.event.repository.default_branch)`.

**Why:** the mirror represents reviewed content. A PR-triggered call would overwrite the mirror with
unmerged text. Any job on the pool *can* read the key (D1), so this protects content integrity, not
the credential. The template's own triggers already satisfy the gate.

**Alternative:** trust the caller's triggers. Rejected because the check is two lines of expression,
and a misconfigured caller fails visibly instead of silently publishing branch content.

### D3. Collection with `git ls-files`, packaging with `git archive`

```text
mapfile -d '' files < <(git ls-files -z -- ':(glob)README.md' ':(glob)docs/**/*.md' ...)
git archive --format=zip -o "$RUNNER_TEMP/docs.zip" HEAD -- "${files[@]}"
```

- `:(glob)` pathspecs use the same `**` semantics as GitHub `paths:` filters, so the template's
  trigger filter and the published set agree. Verified locally: `docs/**/*.md` matches
  `docs/README.md`.
- `git archive` on a pathspec that matches nothing aborts (`fatal: pathspec ... did not match`), so
  the list comes from `git ls-files` (which tolerates misses) and the archive is built from that
  exact list. Verified locally.
- Only tracked files at `HEAD` are included. Generated or untracked files cannot leak in.
- No `zip` binary is needed. The job needs `git`, `curl`, and `jq` (for JSON request bodies and
  response parsing). All three are installed explicitly in the upstream `actions/runner` image that
  `arkanis-runners` uses, and hosted runners have them. `python3` was the first choice, but it is only
  a transitive dependency there. The self-hosted preflight records all three and fails closed if any
  is missing.

Checkout is a normal shallow checkout with `persist-credentials: false`.

### D4. Layout: one import per repo, rooted at the repo name

`to = <target-root>/<github.event.repository.name>`, default root
`viking://resources/arkanis/projects`. The archive keeps repo-relative paths, so directory scopes
work in search (for example `.../Infrastructure/.claude/rules`). The repo name comes from the event
context, so a caller cannot write into another repo's mirror by input. `target-root` stays an input
so a test can publish into a scratch root.

Per-file `kind` tags were dropped. OV applies tags per request, so they would need one import per
subtree and a non-mirrored layout. Path scoping covers the same need.

### D5. Deletions rely on `to` replacement, verified live before rollout

**Outcome (2026-10-25, OpenViking v0.5.2):** deletions propagate. A probe doc deleted in ci#38 left the mirror on the next push-triggered import, so the prune fallback below was not needed.

The docs say absent files "may be removed". That is weaker than the spec's *Deleted doc disappears*
scenario. The live verification (task 7) deletes a doc and re-runs.

- **If the doc disappears:** done. ADR-0008 records the observed behaviour and the OV version.
- **If it does not:** before repo-standards#25 merges, add a prune step. It would list the target
  recursively (`GET /api/v1/fs/ls?recursive=true`) and `DELETE /api/v1/fs` every file absent from
  the archive. That needs the `ci` user to hold delete rights under its target, which is a request
  to the OV owner. The alternative, deleting the whole target before each import, loses unchanged
  vectors and leaves a window with no mirror.

The spec stays mechanism-agnostic, so either outcome satisfies it.

### D6. Empty doc set skips; a size cap fails closed

- No matching files means no import. Importing an empty archive with `to` could wipe the mirror on a
  bad `include-paths`. The repo-standards check is `N/A` for repos without docs, so callers there
  are rare.
- `max-archive-bytes` (default 20 MiB) bounds accidental bulk, for example a committed Markdown data
  dump. The summary lists the largest files.

### D7. Semantic processing, accepted asynchronously

Imports use `semantic_and_vectors`. The first live import used `vectors_only`, and every mirrored
record came back with no abstract. OpenViking's semantic pass writes the abstracts and overviews
that context injection and `find` rank on, so vector-only docs surfaced poorly in practice.

The semantic pass runs on the off-cluster extraction LLM and can take minutes to tens of minutes
per repository. The request therefore sends `wait: false`: OpenViking parses the archive and
queues the semantic and embedding work, then returns. `curl --max-time` bounds that request at
`import-timeout-seconds` (default 120), because OpenViking v0.5.2 rejects a `timeout` body field
(`extra_forbidden`). `published=true` means the import was accepted, not that processing finished.

**Trade-off (accepted on ARK-642):** an upload, parse or permission failure still fails the run, but
an LLM or embedding failure after acceptance only shows in OpenViking's task list
(`GET /api/v1/tasks`). Waiting instead would hold one of the two shared `arkanis-runners` for the
whole LLM pass and fail every docs run during an extraction-host outage. A later import converges
the mirror either way.

### D8. Concurrency per repository and target

```yaml
concurrency:
  group: publish-docs-openviking-${{ github.repository }}-${{ inputs.target-root }}
  cancel-in-progress: false
```

A running import is never cut off mid-request. GitHub keeps at most one pending run per group and
replaces it with newer ones, so the mirror converges on the latest commit. The workflow is added to
`ValidateStateChangingWorkflowConcurrencyContract`.

### D9. Runner defaults point at the only pool that works

`runs-on` defaults to `arkanis-runners` and `runs-on-self-hosted` to `true`. The validator's collapse
expression keeps its literal `'ubuntu-latest'` fallback, which only applies when a caller passes an
empty `runs-on`. On a hosted runner, D1's env check fails closed with a message naming
`arkanis-runners`, so dry runs remain the only hosted use.

### D10. This repo adopts the workflow through the repo-standards template

`.github/workflows/openviking-docs.yml` in `ci` follows the repo-standards template and calls
`ArkanisCorporation/ci/...@v1`. It differs in two places: this repo's validator requires top-level
`permissions: {}` in every workflow (the template uses `contents: read` there, because alint's
`gha-workflow-contents-read` rule expects it in consumers), and it adds the job display name. The full reference is what the adoption check detects; a local
`./` call would leave `ci` FAIL. Its first push run will race the release that moves `v1` and may
fail with a missing workflow. The verification task dispatches it after the release. The PR-time
dogfood job in `release.yml` calls `./.github/workflows/wf-publish-docs-openviking.yml` with
`dry-run: true` on the default runner, like the other fixture jobs.

### Inputs and outputs

| Input | Type | Default |
|---|---|---|
| `runs-on` | string | `arkanis-runners` |
| `runs-on-json` | string | `""` |
| `runs-on-self-hosted` | boolean | `true` |
| `include-paths` | string (newline globs) | the seven standard globs |
| `target-root` | string | `viking://resources/arkanis/projects` |
| `processing-mode` | string (`vectors_only` / `semantic_and_vectors`) | `semantic_and_vectors` |
| `max-archive-bytes` | number | `20971520` |
| `import-timeout-seconds` | number | `120` |
| `dry-run` | boolean | `false` |
| `artifact-retention-days` | number | `14` |
| `upload-diagnostics` | boolean | `true` |
| `timeout-minutes` | number | `15` |

Outputs: `published`, `file-count`, `target-uri`, plus the repo-standard `artifact-manifest` and
`run-metadata` paths. No secrets.

## Risks / Trade-offs

- **[Deletions not propagated]** If OV does not prune, stale docs linger. D5 verifies this before
  rollout and has a prune fallback ready.
- **[Archive root wrapping]** If OV nests the archive under an extra directory (for example
  `docs.zip/`), paths change. Verification checks the stored layout. The fix would be a `source_name`
  or archive-prefix adjustment.
- **[A tool missing on the ARC runner image]** `git`, `curl` and `jq` are explicit packages in the
  upstream runner Dockerfile. If a future image drops one, preflight fails closed with a clear
  message.
- **[Pool contention]** Imports share `arkanis-runners` (`maxRunners: 2`) with deploys. Imports are
  short, the `paths:` filter limits how often they run, and concurrency stops pile-ups. This cost was
  accepted on ARK-642.
- **[`ci` user scope]** Any job on the pool can write anywhere the `ci` user can. The NetworkPolicy
  plus a non-admin key is the accepted boundary (ARK-651).
- **[Doc-path drift with repo-standards]** The list lives in two repos. The validator pins this
  repo's default, repo-standards pins its template, and a mismatch shows up as docs missing from the
  mirror.

## Migration Plan

1. Merge this change with a `feat:` title. semantic-release moves `v1`.
2. Dispatch `ci`'s own `openviking-docs` workflow and run the live verification (task 7). Add the
   prune step first if deletions do not propagate.
3. Merge repo-standards#25. Its enforcement run starts opening capped adoption PRs. If it lands
   before a Monday 07:30 UTC run, dispatch it with a small cap instead of waiting.
4. **Rollback:** disable repo-standards' enforcement run to stop new adoption. Already-adopted
   callers can be disabled per repo in the Actions UI. Removing the workflow from `v1` would make
   every caller fail loudly, so prefer a fix-forward release. Mirrored data can be removed with
   `DELETE /api/v1/fs?uri=viking://resources/arkanis/projects/&recursive=true` using an admin key.
