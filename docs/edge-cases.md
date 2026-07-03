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
| K8s deploy race | Env concurrency group + queued deploys |
