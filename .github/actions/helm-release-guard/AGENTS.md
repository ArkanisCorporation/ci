# helm-release-guard/AGENTS.md

## Scope

Release recording, rollback, diagnostics, and the schema rollback hook around a Helm deployment this platform does not control.

## Rules

- A failed deployment must fail the job. The finalize phase exits non-zero for every outcome except `deployed` and `not-attempted`.
- Collect diagnostics before the rollback; the rollback removes the failing pods and their logs.
- Render the schema rollback Job before the Helm rollback and create it only after the rollback succeeded.
- Never run a schema rollback CronJob this deployment did not replace: it still belongs to the release being restored.
- "Replaced" is uid OR generation differing from the captured value. Both CronJob shapes are supported: a Helm hook (`pre-install,pre-upgrade` + `before-hook-creation`, recommended) gets a new uid at generation 1 on every upgrade; an ordinary chart resource keeps its uid and bumps its generation. Never compare the generation alone.
- Never read Secret objects, `helm get values`, `helm get manifest`, or `helm status --output json`. Never store pod environment values.
- Record the deployment start first in the capture phase and inject it as `ARKANIS_DEPLOY_STARTED_AT` (constant `DeployStartedAtVariable`, RFC 3339 UTC, second precision) into every container and init container of the schema Job, replacing a chart entry of that name. Consumers rely on it to leave earlier deploys' schema changes alone; never run the Job without it.
- Keep the rendered Job manifest under `RUNNER_TEMP`, out of the uploaded report.
- No NuGet package references in `helm-release-guard.cs`: a rollback must not wait on a package restore.
- Bound every Helm and kubectl call and every wait.
- No destructive recovery by guesswork: a stuck first install stops the job, it is not uninstalled.
- Every decision change needs a scenario in `tests/helm-release-guard.cs`. Its fake `helm list`, `helm history`, and kubectl JSON mirror real Helm 4.3.1 output; re-check them with `tests/live/helm-release-guard-kind.sh` (opt-in, needs kind) when Helm is bumped.
- Delete `helm-timeout-shim.sh` once `aspire deploy` exposes its Helm arguments.
- Exclusive mode is opt-in (`DEPLOY_GUARD_EXCLUSIVE=true`). The stop is the last act of the capture phase: record replica counts in the state file before the first `kubectl scale`, wait until no selected pod exists (terminating pods count), and on any failure restore the counts, verify, and exit non-zero so the deploy never runs.
- Never stop StatefulSets: they are the data tier the migrator needs.
- In exclusive mode the failure path is diagnostics, render, stop the failed release, schema Job, `helm rollback --wait`, restore recorded replicas, verify. Never run the schema Job after the rollback there: the previous version cannot become ready on the unreverted schema.
- Never assume Helm restored the replica counts; read each recorded Deployment and scale it when it is not at its count.
- `ARKANIS_DEPLOY_EXCLUSIVE` (constant `DeployExclusiveVariable`) is exported by the workflow's deploy step only, gated on the capture output `exclusive=true`. The guard never sets it anywhere else.
- Never pass `--request-timeout` to kubectl. The flag makes kubectl ignore in-cluster ServiceAccount credentials and dial `localhost:8080`, which breaks every call on a runner pod without a kubeconfig while Helm keeps working. The process deadline in `Run` bounds each call instead. `tests/helm-release-guard.cs` fails on a call that carries the flag.
