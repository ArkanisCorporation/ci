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
- Every decision change needs a scenario in `tests/helm-release-guard.cs`.
- Delete `helm-timeout-shim.sh` once `aspire deploy` exposes its Helm arguments.
