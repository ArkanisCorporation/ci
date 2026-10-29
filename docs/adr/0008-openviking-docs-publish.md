# ADR-0008: OpenViking docs publish from in-cluster runners

Status: accepted

Audience: platform maintainers, security reviewers, and repository owners adopting the docs mirror.
Last verified: 2026-10-29.

## Context

ARK-642 mirrors every Arkanis repository's docs into OpenViking, so an agent working in one repository can find another repository's knowledge by search.
Git stays the source of truth and OpenViking holds a read-only copy.
OpenViking's API is a ClusterIP service with no public endpoint; its Tailscale ingress is admin-only.
The organization is on GitHub Free, where organization Actions secrets are unavailable to private repositories, so a per-repository secret would mean manual setup in every consumer.

## Decision

- **Reachability:** publishing runs on the in-cluster `arkanis-runners` ARC pool, whose pods a NetworkPolicy admits to OpenViking (ARK-651).
  A Tailscale step on GitHub-hosted runners was rejected: it needs an ACL grant to the cluster ingress tag and would expose every internal admin UI to CI.
- **Identity:** a dedicated non-admin OpenViking user `ci`, limited to importing resources (ARK-651).
- **Credential delivery:** the runner pool exposes `OPENVIKING_URL` and `OPENVIKING_API_KEY` as plain container env from 1Password via External Secrets (ARK-652), following *Runner-Advertised Capabilities* in [runner-contract.md](../runner-contract.md).
  The workflow takes no secret input. Because GitHub does not mask plain env, the workflow masks the key itself and passes it to `curl` through a config on stdin.
  Any job on the pool can read the key; that is accepted because the pool already runs with the deployer service account, and the key is non-admin.
- **Publishing gate:** imports run only for pushes or dispatches on the caller's default branch, never for pull request events, so the mirror only ever holds reviewed content. Dry runs are allowed anywhere.
- **Layout:** one import per run, `to = viking://resources/arkanis/projects/<repository name>`, preserving repository-relative paths so directory scopes work in search. The repository name comes from the event context, not caller input.
  Tags are `repo=<name>` and `commit=<sha>`; per-subtree tags were rejected because OpenViking applies tags per request.
- **Processing:** `semantic_and_vectors`, so mirrored docs get the LLM abstracts and overviews that retrieval ranks on. The import is accepted asynchronously (`wait: false`), so the LLM pass never holds a shared runner; failures after acceptance show in OpenViking's task list, not in CI. On v0.5.2 that includes parse errors: with `wait: false` the server returns a task id before parsing.
- **Packaging:** `git ls-files` with `:(glob)` pathspecs selects tracked docs with GitHub `paths:` semantics; `git archive` zips exactly that list. An empty selection skips the import instead of wiping the mirror.
- **Deletions:** each import replaces the `to` target, and OpenViking v0.5.2 removes files absent from the re-import (verified below), so the workflow has no prune step.
- **Adoption:** `ArkanisCorporation/repo-standards` owns the caller template, the adoption check, and the adoption PRs. The default doc paths are a contract between both repositories, pinned by validators on each side. Repositories opt out with the `openviking-docs-optout` topic.

## Consequences

Consumers adopt with one caller file and no secrets or inputs.
The workflow only works on `arkanis-runners`; elsewhere it can only dry-run.
Imports share the pool's two runners with deployments; the callers' `paths:` filter and per-repository concurrency keep that load small.
Mirror content is visible to every user of the OpenViking account, which is why the opt-out topic exists.

## Live verification

Observed against OpenViking v0.5.2 on 2026-10-25 (ci v1.7.1, [run 40196559396](https://github.com/ArkanisCorporation/ci/actions/runs/40196559396)):

- **API:** `add_resource` rejects unknown body fields and has no server-side `timeout`, so the request carries `wait: true` and curl bounds the wait (ci v1.7.1).
- **First import:** 37 files parsed by `MarkdownParser`, 77 contexts embedded, about 18 seconds, `vectors_only` with no semantic calls. Search hits had no abstracts, so retrieval ranked them poorly.
- **Semantic import (ci v1.7.2, [run 40199205439](https://github.com/ArkanisCorporation/ci/actions/runs/40199205439)):** the request returned within a second with only a `task_id`, so parsing is asynchronous too. The semantic pass on `ollama/qwen3:4b-instruct-2507-q8_0` then took about 12 minutes for 37 docs (about 130 semantic nodes, no errors) and wrote abstracts plus a `.overview.md` per directory. Summaries from that small model can misstate details (one called Tailscale a feature of this design), so treat them as navigation aids and read the source section.
- **Layout:** no archive-root directory. Each Markdown file becomes a directory named after the file without `.md`, holding its sections, for example `.../ci/docs/adr/0008-openviking-docs-publish/`. Directory scopes such as `.../ci/docs/adr` work as intended.
- **Scoped search:** a search under `.../projects/ci` returns the matching catalog section and ADR first.
- **Push trigger:** merging a doc change starts the caller's `openviking-docs` run without a dispatch ([run 40222234309](https://github.com/ArkanisCorporation/ci/actions/runs/40222234309)).
- **Deletion propagation:** a probe doc added in ci#36 appeared at `.../ci/docs/openviking-mirror-probe/`; after ci#38 deleted it, the push-triggered re-import ([run 40225284968](https://github.com/ArkanisCorporation/ci/actions/runs/40225284968)) removed it from both the listing and search.
- **Tags:** the server accepts `repo=<name>` and `commit=<sha>`, but no user-facing tool shows stored tags, so they are not independently confirmed.

Related: ARK-642, ARK-651, ARK-652.
