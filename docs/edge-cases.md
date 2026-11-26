# Edge cases

Audience: agents + maintainers.

| Case | Rule |
|---|---|
| Caller env missing in called workflow | Pass via `with`, not ambient env |
| Environment secrets with `workflow_call` | Avoid; environment secrets do not pass like normal secrets |
| Nested secrets | Pass explicitly each hop |
| Matrix reusable outputs | Do not aggregate via outputs; use JSON artifact |
| Dynamic reusable workflow `uses` | Not supported; use finite wrappers |
| Required check skipped by paths | Use always-run aggregator |
| Fork PR needs labels/comments | Use metadata-only workflow, no checkout |
| Docker provenance missing | Check exporter/load/push mode |
| Hidden files absent from artifact | Expected unless allowlisted |
| Self-host stale runner | Update fleet; preflight version |
| Node action runtime warning | Update action version/SHA to Node24-compatible |
| Cache poisoning concern | Exact keys, no secret cache, trusted warmers |
| Release rerun duplicates | Idempotent publish flags + digest check |
| Release/publish/deploy race | Job-level concurrency by protected target |
| Obsolete queued main validation | Per-lane concurrency replaces only the prior pending job; running validation and release work continue |
| K8s deploy race | Environment and namespace concurrency group |
| K8s deploy fails or is cancelled mid-upgrade | `helm-release-guard` rolls back to the last deployed revision; a release left `pending-*` is recovered at the start of the next run |
| Release's migration breaks the previous version | Migrator refuses it in a rolling deploy; `exclusive: true` stops the previous version first and exports `ARKANIS_DEPLOY_EXCLUSIVE=true` (downtime); prefer expand/contract (ARK-746) |
| Older release still present after a verified deploy | `schema-confirm` is `blocked` (warning, green) while any object in the namespace runs or can revive an older release's image; the consumer's contract migrations stay deferred (ARK-746) |
| Tool hardcodes its Helm arguments (`aspire deploy`) | `helm` shim first on `PATH` for that one step appends `--timeout`; delete it when the tool exposes the flags |
| Queued concurrency backlog | Add `queue: max` after pinned actionlint supports it |
| Artifact storage quota hit | Uploads are best-effort (`continue-on-error: true`); set `upload-diagnostics: false` to stop uploading |
| Job or service containers on Docker-out-of-Docker runners | Avoid; runner externals are missing in job containers. Use `docker run` and reach published ports through the bridge gateway |
