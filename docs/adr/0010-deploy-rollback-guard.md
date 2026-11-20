# ADR-0010: Deploy rollback guard around `aspire deploy`

Status: accepted

Audience: platform maintainers, release managers, and consumers of `wf-deploy-k8s-aspire.yml`.
Last verified: 2026-11-20.

## Context

Standard for every Arkanis project: a deployment must not be able to result in a broken environment.
`wf-deploy-k8s-aspire.yml` runs `dotnet tool run aspire -- deploy`.
Aspire.Hosting.Kubernetes 13.5.3 hardcodes `helm upgrade --install <release> "<outputPath>" --namespace <ns> --create-namespace --wait [-f values...]`.
It sets no `--timeout`, so Helm's 5 minute default applies, and no `--rollback-on-failure`.
The argument list cannot be extended; the Helm runner is internal.
With `maxUnavailable: 0` old pods keep serving when new pods fail, but the release stays failed and half-rolled, nothing reverts it, and a failed deploy uploaded no pod diagnostics.
A run cancelled mid-upgrade leaves the release `pending-upgrade`, which makes every later `helm upgrade` fail.

## Decision

Keep `aspire deploy` and guard it from outside with a composite action, `helm-release-guard`, called before and after the deploy step.

- **Record first.** `helm list` and `helm history` give the current revision and the last `deployed` revision. An unreadable release stops the job before anything is deployed.
- **Recover a stuck release up front.** A `pending-*` release is rolled back to its last `deployed` revision. A stuck first install has no such revision and stops the job; it is never uninstalled automatically.
- **Bound the Helm wait with a shim.** A Bash `helm` shim, first on `PATH` for the deploy step only, appends `--timeout <rollout-timeout>` to `helm upgrade`. It is the one piece that exists only because Aspire's arguments are closed.
- **Verify on success.** `kubectl rollout status` for every Deployment carrying the release's `meta.helm.sh/release-name` annotation. A failed verification is a failed deployment.
- **On failure: diagnostics, rollback, schema hook, verification, in that order.** Diagnostics come first because the rollback deletes the failing pods.
- **Schema rollback by convention.** A suspended CronJob `schema-rollback` in the consumer chart, belonging to the release being deployed. Its Job is rendered before the Helm rollback and created after it. It is not run when this deployment did not replace the CronJob.
- **Two CronJob shapes, hook recommended.** As a Helm hook (`pre-install,pre-upgrade` with `before-hook-creation`) Helm recreates it before any workload of the new release, so it exists even when the upgrade dies part-way and `helm rollback` leaves it alone. As an ordinary chart resource it is patched in place and reverted by the rollback. "Replaced by this deployment" therefore means a different `metadata.uid` (hook) or a different `metadata.generation` (ordinary resource); the generation alone is `1` for every recreated hook.
- **The job always fails after a failed deployment.** Rollback restores the environment; it does not make the run green.
- **Safe by default.** `rollback-on-failure` defaults to `true`, `rollout-timeout` to `15m`, `timeout-minutes` rises from 45 to 60.
- **Exclusive mode for breaking migrations, opt-in.** Rolling deploys keep the previous version serving while the new migrator runs, which is only safe for backwards-compatible migrations, so consumers mark breaking migrations and their migrator refuses them. `exclusive: true` (default `false`) is the deliberate, downtime-taking way to ship one: after capture and recovery the guard records the `spec.replicas` of every Deployment the release owns, scales them to 0, and waits, bounded by `rollout-timeout`, until none of their pods exists (terminating pods count). Only then does the deploy step run, with `ARKANIS_DEPLOY_EXCLUSIVE=true` exported into it alone, the consumer's permission to apply the breaking migration. A failed stop deploys nothing, restores the recorded counts, verifies, and reports `failed-exclusive-stop`.
- **Exclusive failure path reverts the schema before the Helm rollback.** The previous version cannot run on the failed release's schema, so `helm rollback --wait` would wait forever and the schema Job would never run. The guard stops the failed release instead, runs the schema Job, rolls back, then checks every recorded Deployment and scales the ones Helm did not restore. Helm 4.3.1 re-applies the replica counts on upgrade and rollback after `kubectl scale` (verified live), but that is checked, not assumed. The summary states the downtime window, or that the service is still down.
- **StatefulSets are not stopped.** In an Aspire chart they are the data tier the migrator needs; the code whose schema expectations break runs in Deployments.

Rejected:

- Replacing `aspire deploy` with `aspire publish` plus our own `helm upgrade --rollback-on-failure`. It gives full control over Helm but forks from Aspire's deploy pipeline (image build and push, parameter resolution) and still needs the schema hook and the diagnostics.
- Helm hooks (`pre-rollback`, `post-rollback`) for the schema rollback. Helm runs the hooks of the revision it rolls back to, which is the previous release; only the failed release knows which schema changes to undo.
- Uninstalling a stuck first install automatically. It can delete data-bearing resources on a wrong guess.
- Exclusive by default, or exclusive whenever a migration is pending. Every exclusive deploy is an outage; the downtime has to be a decision. Expand/contract across releases stays the preferred way (ARK-746).
- Exclusive mode keeping the rolling order (rollback, then schema Job). See above: the rollback's wait cannot succeed before the schema is reverted.
- Recording the replica counts in a Deployment annotation. It would survive a runner that dies mid-stop, but writes to consumer objects the chart owns; the state file is enough for every case the job itself survives.

## Consequences

Existing callers get rollback, a 15 minute Helm wait, rollout verification, and failure diagnostics without changing inputs.
The deploy job now needs Bash on Linux or macOS, and its kube identity must be able to roll back and to create and delete Jobs.
The guard assumes the release is named after the lowercased `aspire-environment`. When the AppHost names it differently the shim's record of the real `helm upgrade` is used and a warning asks for `helm-release-name`; only up-front recovery of a stuck release is lost until it is set.
A Deployment the release owns that was already unhealthy before the deploy now fails the deployment.
Cancellation and job timeouts are not rolled back in the same run; the next run recovers the release first.
Helm reverts manifests, not data: without a `schema-rollback` hook, consumers must keep migrations compatible with the previous release.
A hook-shaped CronJob is recreated on every upgrade, including a redeploy of an already deployed version, so the schema Job also runs when such a redeploy fails.
The migrator then finds its own record from the earlier, successful deploy and cannot tell it from this attempt's.
The guard therefore records when the attempt began and passes it to every container of the Job as `ARKANIS_DEPLOY_STARTED_AT` (RFC 3339 UTC, second precision). The hook must leave alone every schema change recorded before that instant and succeed doing nothing when there is no later one.
The value is the runner's clock, read before any image build or Helm call; a consumer comparing it with database time must not subtract a margin from it.
A deployment that passes `--no-hooks` never replaces a hook-shaped CronJob. `aspire deploy` does not pass it.
An exclusive deployment adds a third `rollout-timeout` to the worst case (the stop), so the job timeout warning uses three times `rollout-timeout` plus ten minutes for it. Its kube identity also needs `get` and `patch` on `deployments/scale`.
A runner that dies after the stop and before the finalize phase leaves the Deployments at 0 with the counts only in the lost state file; the next deploy re-applies the chart's counts, but a manual rollback must scale them by hand.

## Migration

None required.
Optional: pass `helm-release-name` when the AppHost sets a release name, add the `schema-rollback` CronJob to the chart, and read the `outcome` output from an `if: always()` job to alert on `rollback-failed`.
Consumers with breaking migrations: make the migrator refuse them unless `ARKANIS_DEPLOY_EXCLUSIVE` is `true`, and pass `exclusive: true` for the one release that needs it.
Callers that set `timeout-minutes` below twice `rollout-timeout` plus ten minutes get a warning and should raise it.
Remove the shim when Aspire exposes the Helm arguments, and consider `--rollback-on-failure` then.

## References

- Consumer contract and outcomes: [deployments.md](../deployments.md#rollback)
- Helm upgrade: https://helm.sh/docs/helm/helm_upgrade/
- Helm rollback: https://helm.sh/docs/helm/helm_rollback/
- Helm history: https://helm.sh/docs/helm/helm_history/
- kubectl create job: https://kubernetes.io/docs/reference/kubectl/generated/kubectl_create/kubectl_create_job/
- kubectl scale: https://kubernetes.io/docs/reference/kubectl/generated/kubectl_scale/
- Rolling versus exclusive: [deployments.md](../deployments.md#rolling-versus-exclusive)
