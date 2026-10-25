# Proposal

## Why

ARK-642 mirrors every Arkanis repo's docs into OpenViking (OV) so any agent in any repo can find
another repo's knowledge by search. Git stays the source of truth; OV holds a read-only copy. This
repo owns the piece every consumer calls: a reusable workflow that publishes a repo's docs on merge
to the default branch. `repo-standards` (repo-standards#25) measures adoption and opens the adoption
PRs; its rollout guard waits for this workflow to exist at `@v1`.

The infrastructure is in place. `arkanis-runners` pods reach OV over ClusterIP (ARK-651), and the
pool exposes `OPENVIKING_URL` and `OPENVIKING_API_KEY` to every job (ARK-652), so callers need no
secrets.

## What Changes

- **New public workflow `wf-publish-docs-openviking.yml`** (`on.workflow_call`):
  - collects tracked Markdown docs matching `include-paths` (default: the standard doc paths) with
    `git ls-files`, then zips exactly that list with `git archive`;
  - uploads it (`POST /api/v1/resources/temp_upload`) and imports it with `to=<target-root>/<repo>`
    (`POST /api/v1/resources`), so each run replaces the repo's mirror, with `processing_mode`
    `vectors_only` and tags `repo=<repo>`, `commit=<sha>`;
  - reads the key and URL from the runner environment, masks the key, and fails closed when they
    are absent, unless `dry-run`;
  - publishes only from the caller's default branch, and refuses `pull_request` events;
  - skips the import (not a wipe) when no docs match;
  - writes a step summary, `run-metadata.json`, and diagnostics; serializes runs per repository.
- **Runner defaults:** `runs-on: arkanis-runners`, `runs-on-self-hosted: true`. That is the only pool
  that can reach OV, so the defaults make a caller with no inputs work.
- **Contract files:** input schema, consumer fixture, validator entries (job and step display names,
  concurrency, doc-path default), README row, workflow catalog section, workflows `AGENTS.md` list.
- **Dry-run dogfood** job in `release.yml` on pull requests, proving collection and archiving with
  no OV access.
- **This repo adopts it:** `.github/workflows/openviking-docs.yml`, a copy of the repo-standards
  caller template, so `ci`'s own docs are mirrored and the adoption check passes.
- **ADR-0008** records reachability, identity, layout, and the reliance on `to` replacement for
  deletions.
- **Live verification** before repo-standards#25 merges: first import, layout, tags, search, and
  that a deleted doc disappears from the mirror. If it does not, a prune step is added first.

## Capabilities

### New Capabilities

- `openviking-docs-publish`: how a caller's docs are selected, packaged, and imported into
  OpenViking, which events and refs may publish, how credentials are obtained and protected, and
  what a run reports.

### Modified Capabilities

(none: `openspec/specs/` is empty)

## Impact

- **Public API:** one new workflow plus its inputs and outputs, an additive `feat:` release that moves
  `v1`. No existing workflow changes.
- **Trust zone:** *publish*. It writes to an external store from a protected default-branch push,
  using a runner-provided credential instead of OIDC (see design).
- **Cross-repo contract:** the default `include-paths` must equal repo-standards'
  `OPENVIKING_DOC_GLOBS` and its caller template's `paths:` filter.
- **Rollout order (human):** merge this, let semantic-release move `v1`, verify live, then merge
  repo-standards#25. Its first scheduled run is Monday 07:30 UTC.
- **Out of scope:** the adoption check, template, and PRs (repo-standards); curated shared knowledge
  under other `viking://resources/` subtrees (ARK-616, ARK-617).
