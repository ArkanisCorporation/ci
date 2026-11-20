#!/usr/bin/env bash
# Summary:
#   Opt-in live proof of helm-release-guard and helm-timeout-shim against a throwaway kind cluster.
# Remarks:
#   NOT part of the default suite (tests/AGENTS.md: no cluster in default tests) and not run by the selftest.
#   Run it by hand after changing the guard or bumping Helm:  bash tests/live/helm-release-guard-kind.sh
#   Needs docker, kind, helm, kubectl, and the .NET 10 SDK. Takes about five minutes.
#
#   It never touches the caller's kube context: the cluster gets its own kubeconfig file in a temp directory,
#   KUBECONFIG points at that file for every command, and the script aborts unless the current context is the
#   kind cluster it just created. The cluster is deleted on exit, also on failure.
#
#   The "deploy step" is `helm upgrade --install <release> <chart> --namespace <ns> --create-namespace --wait`
#   through the shim on PATH: the argument shape `aspire deploy` uses. The guard is driven with the same
#   environment contract as action.yml. EXCLUSIVE=true before `run` drives the guard in exclusive mode and asserts
#   that no pod of the release exists any more when the deploy step starts.
set -euo pipefail

repo="$(cd "$(dirname "$0")/../.." && pwd)"
cluster="ci-guard-proof-$$"
work="$(mktemp -d)"
export KUBECONFIG="$work/kubeconfig"
chart="$repo/tests/live/helm-release-guard-chart"
guard_script="$repo/.github/actions/helm-release-guard/helm-release-guard.cs"
rollout_timeout="45s"

cleanup() {
  kind delete cluster --name "$cluster" --kubeconfig "$KUBECONFIG" > /dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

timeout 400 kind create cluster --name "$cluster" --kubeconfig "$KUBECONFIG" --wait 180s
if [[ "$(kubectl config current-context)" != "kind-$cluster" ]]; then
  echo "ABORT: current context is not kind-$cluster" >&2
  exit 99
fi
real_helm="$(command -v helm)"

fail() { echo "FAIL: $*" >&2; exit 1; }
output() { grep "^$2=" "$work/$1/output-finalize.txt" | tail -1 | cut -d= -f2-; }

# guard <scenario> <namespace> <phase> [deploy-outcome]
guard() {
  local dir="$work/$1"
  mkdir -p "$dir/workspace" "$dir/runner-temp/helm-shim"
  : > "$dir/output-$3.txt"
  (cd "$dir/workspace" && DEPLOY_GUARD_PHASE="$3" DEPLOY_GUARD_NAMESPACE="$2" DEPLOY_GUARD_ASPIRE_ENVIRONMENT=Proof \
    DEPLOY_GUARD_RELEASE_NAME="" DEPLOY_GUARD_ROLLOUT_TIMEOUT="$rollout_timeout" DEPLOY_GUARD_ROLLBACK_ON_FAILURE=true \
    DEPLOY_GUARD_EXCLUSIVE="${EXCLUSIVE:-false}" \
    DEPLOY_GUARD_SCHEMA_ROLLBACK_CRONJOB=schema-rollback DEPLOY_GUARD_REPORT_DIR=artifacts/deploy-report \
    DEPLOY_GUARD_DEPLOY_OUTCOME="${4:-}" DEPLOY_GUARD_SHIM_RECORD="$dir/runner-temp/helm-shim/upgrade-record" \
    DEPLOY_GUARD_JOB_TIMEOUT_MINUTES=60 GITHUB_WORKSPACE="$dir/workspace" RUNNER_TEMP="$dir/runner-temp" \
    GITHUB_RUN_ID="$1" GITHUB_RUN_ATTEMPT=1 GITHUB_OUTPUT="$dir/output-$3.txt" GITHUB_STEP_SUMMARY="$dir/summary.md" \
    timeout 600 dotnet run --file "$guard_script" --no-launch-profile) > "$work/$1/$3.log" 2>&1
}

# run <scenario> <namespace> <expected guard exit: zero|nonzero> <helm --set arguments...>
run() {
  local scenario="$1" ns="$2" expected="$3" shim="$work/$1/runner-temp/helm-shim" outcome=success code=0
  shift 3
  guard "$scenario" "$ns" capture || fail "$scenario: capture failed: $(tail -3 "$work/$scenario/capture.log")"
  if [[ "${EXCLUSIVE:-false}" == true ]]; then
    grep -q '^exclusive=true' "$work/$scenario/output-capture.txt" || fail "$scenario: capture did not confirm the exclusive stop"
    [[ -z "$(kubectl get pods --namespace "$ns" --selector app=web --output name)" ]] || fail "$scenario: pods of the previous version still exist when the deploy starts"
    [[ "$(kubectl get deployment web --namespace "$ns" --output jsonpath='{.spec.replicas}')" == 0 ]] || fail "$scenario: web is not scaled to 0 before the deploy"
  fi
  install -m 0755 "$repo/.github/actions/helm-release-guard/helm-timeout-shim.sh" "$shim/helm"
  (export ARKANIS_REAL_HELM="$real_helm" ARKANIS_HELM_UPGRADE_TIMEOUT="$rollout_timeout" ARKANIS_HELM_SHIM_RECORD="$shim/upgrade-record" PATH="$shim:$PATH"
    timeout 300 helm upgrade --install proof "$chart" --namespace "$ns" --create-namespace --wait "$@") > "$work/$scenario/deploy.log" 2>&1 || outcome=failure
  guard "$scenario" "$ns" finalize "$outcome" || code=$?
  if [[ "$expected" == zero && "$code" != 0 ]] || [[ "$expected" == nonzero && "$code" == 0 ]]; then
    fail "$scenario: guard exit $code, expected $expected: $(tail -5 "$work/$scenario/finalize.log")"
  fi
  echo "$scenario: outcome=$(output "$scenario" outcome) previous=$(output "$scenario" previous-revision) new=$(output "$scenario" new-revision) current=$(output "$scenario" current-revision) schema-rollback=$(output "$scenario" schema-rollback)"
}
expect() { [[ "$(output "$1" "$2")" == "$3" ]] || fail "$1: $2 is '$(output "$1" "$2")', expected '$3'"; }
marker() { kubectl get deployment web --namespace "$1" --output jsonpath='{.spec.template.spec.containers[0].env[0].value}'; }
job_log() { cat "$work/$1/workspace/artifacts/deploy-report/diagnostics/schema-rollback/job-logs.txt"; }

# 1. First install.
run install hook zero --set marker=v1
expect install outcome deployed

# 2. Broken upgrade, Helm hook CronJob (recreated: new uid, generation 1): rolled back, hook ran after the rollback.
run broken hook nonzero --set marker=v2 --set ready=false
expect broken outcome rolled-back
expect broken schema-rollback succeeded
[[ "$(marker hook)" == v1 ]] || fail "broken: workload was not restored to v1"
started="$(grep '^deploy-started-at=' "$work/broken/output-capture.txt" | cut -d= -f2-)"
job_log broken | grep -q "marker=v2 started_at=$started" || fail "broken: schema Job did not run the failed release's hook with ARKANIS_DEPLOY_STARTED_AT: $(job_log broken)"
grep -q 'ready=1' <<< "ready=$(kubectl get deployment web --namespace hook --output jsonpath='{.status.readyReplicas}')" || fail "broken: no ready replica after rollback"
[[ -f "$work/broken/workspace/artifacts/deploy-report/diagnostics/before-rollback/helm-status.txt" ]] || fail "broken: diagnostics missing"
if grep -rq "fixture-secret-literal-not-real\|fixture-env-literal\|Zml4dHVyZS1zZWNyZXQ" "$work/broken"; then fail "broken: Secret or environment values leaked into the report or logs"; fi

# 3. A rolled-back release is not wedged.
run recover hook zero --set marker=v3
expect recover outcome deployed

# 4. Release left pending-upgrade by a killed helm is recovered before the next deploy.
helm upgrade --install proof "$chart" --namespace hook --wait --timeout 60s --set marker=v4 --set ready=false > /dev/null 2>&1 &
helm_pid=$!
for _ in $(seq 1 30); do
  [[ "$(helm status proof --namespace hook | awk '/^STATUS/{print $2}')" == pending-upgrade ]] && break
  sleep 1
done
sleep 2
kill -KILL "$helm_pid"
wait "$helm_pid" 2> /dev/null || true
[[ "$(helm status proof --namespace hook | awk '/^STATUS/{print $2}')" == pending-upgrade ]] || fail "stuck: release is not pending-upgrade"
run stuck hook zero --set marker=v5
expect stuck outcome deployed
grep -q '^recovered=true' "$work/stuck/output-capture.txt" || fail "stuck: capture did not recover the release"

# 5. First install fails: nothing to roll back to.
run firstfail fresh nonzero --set marker=v1 --set ready=false
expect firstfail outcome failed-no-previous-release
if grep -q '^\$ helm rollback' "$work/firstfail/finalize.log"; then fail "firstfail: a rollback was attempted"; fi

# 6. Ordinary chart-resource CronJob (same uid, generation bumps; reverted by the rollback).
run plaininstall plain zero --set marker=v1 --set hookShape=false
run plainbroken plain nonzero --set marker=v2 --set ready=false --set hookShape=false
expect plainbroken outcome rolled-back
job_log plainbroken | grep -q "marker=v2" || fail "plainbroken: schema Job did not run the failed release's CronJob"

# 7. Schema Job fails: workloads restored, outcome says the environment needs attention.
run schemafail plain nonzero --set marker=v3 --set ready=false --set hookShape=false --set schemaExit=1
expect schemafail outcome rollback-failed
expect schemafail schema-rollback failed
[[ "$(marker plain)" == v1 ]] || fail "schemafail: workload was not restored"

# 8. Exclusive deployment succeeds: the previous version is stopped before the upgrade, Helm brings the replicas back.
run exclusiveinstall excl zero --set marker=v1
EXCLUSIVE=true run exclusive excl zero --set marker=v2
expect exclusive outcome deployed
[[ "$(marker excl)" == v2 ]] || fail "exclusive: workload is not v2"
[[ "$(kubectl get deployment web --namespace excl --output jsonpath='{.spec.replicas}/{.status.readyReplicas}')" == 1/1 ]] || fail "exclusive: replicas not restored"
[[ -n "$(output exclusive downtime-started-at)" && -n "$(output exclusive downtime-ended-at)" ]] || fail "exclusive: downtime window missing"
grep -q "This deployment was exclusive: the service was down from $(output exclusive downtime-started-at) to $(output exclusive downtime-ended-at)." "$work/exclusive/summary.md" \
  || fail "exclusive: summary does not state the downtime window"

# 9. Exclusive deployment fails: failed release stopped, schema Job run, rolled back, replicas restored.
EXCLUSIVE=true run exclusivebroken excl nonzero --set marker=v3 --set ready=false
expect exclusivebroken outcome rolled-back
expect exclusivebroken schema-rollback succeeded
[[ "$(marker excl)" == v2 ]] || fail "exclusivebroken: workload was not restored to v2"
[[ "$(kubectl get deployment web --namespace excl --output jsonpath='{.spec.replicas}/{.status.readyReplicas}')" == 1/1 ]] || fail "exclusivebroken: replicas not restored"
job_log exclusivebroken | grep -q "marker=v3" || fail "exclusivebroken: schema Job did not run the failed release's hook"
stop_line="$(grep -n 'Stop Deployments before the schema rollback' "$work/exclusivebroken/finalize.log" | head -1 | cut -d: -f1)"
rollback_line="$(grep -n '^\$ helm rollback' "$work/exclusivebroken/finalize.log" | head -1 | cut -d: -f1)"
[[ -n "$stop_line" && -n "$rollback_line" && "$stop_line" -lt "$rollback_line" ]] || fail "exclusivebroken: the failed release was not stopped before the rollback"
[[ -n "$(output exclusivebroken downtime-ended-at)" ]] || fail "exclusivebroken: downtime did not end"
grep -q "This deployment was exclusive: the service was down from" "$work/exclusivebroken/summary.md" || fail "exclusivebroken: summary does not state the downtime"

echo "Helm release guard live proof passed ($(helm version --short), $(kubectl version --output json 2> /dev/null | grep -o '"gitVersion": *"[^"]*"' | tail -1))."
