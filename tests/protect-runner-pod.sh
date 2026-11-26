#!/usr/bin/env bash
# Summary:
#   Behavior test for .github/actions/helm-release-guard/protect-runner-pod.sh.
# Remarks:
#   No cluster: a fake kubectl on PATH records every call and answers from fixture files.

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
script="${root}/.github/actions/helm-release-guard/protect-runner-pod.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fail() { echo "FAILED: $1" >&2; [[ -n "${2:-}" ]] && echo "$2" >&2; exit 1; }

mkdir -p "${work}/bin"
cat > "${work}/bin/kubectl" <<'FAKE'
#!/usr/bin/env bash
dir="$FAKE_DIR"
echo "$*" >> "${dir}/calls"
case "$*" in
  *" label pod "*) [[ -f "${dir}/label-forbidden" ]] && { echo "Error from server (Forbidden)" >&2; exit 1; }; echo "pod labeled" ;;
  *" get pod "*) cat "${dir}/node-name" 2> /dev/null ;;
  *" get node "*) [[ -f "${dir}/node-unreadable" ]] && exit 1; cat "${dir}/unschedulable" 2> /dev/null || true ;;
esac
FAKE
chmod +x "${work}/bin/kubectl"

# Runs the script in a fresh scenario directory. Arguments: scenario name, then fixture files to create as name=content.
run() {
  scenario="${work}/$1"; shift
  mkdir -p "${scenario}/sa"
  echo "arc-runners" > "${scenario}/sa/namespace"; echo "not-a-real-token" > "${scenario}/sa/token"; : > "${scenario}/sa/ca.crt"
  echo "node-a" > "${scenario}/node-name"; : > "${scenario}/calls"
  for fixture in "$@"; do echo "${fixture#*=}" > "${scenario}/${fixture%%=*}"; done
  set +e
  output="$(FAKE_DIR="$scenario" PATH="${work}/bin:${PATH}" RUNNER_TEMP="$scenario" KUBECONFIG="/other/cluster" \
    KUBERNETES_SERVICE_HOST="${in_cluster-10.0.0.1}" ARKANIS_RUNNER_SERVICE_ACCOUNT_DIR="${scenario}/sa" ARKANIS_RUNNER_POD_NAME="runner-1" \
    bash "$script" 2>&1)"
  status=$?
  set -e
  calls="$(< "${scenario}/calls")"
}

run schedulable
[[ $status -eq 0 ]] || fail "a runner on a schedulable node must pass" "$output"
[[ "$calls" == *"label pod runner-1 --namespace arc-runners arkanis.dev/deploy-in-progress=true --overwrite"* ]] || fail "the runner pod must be labelled" "$calls"
[[ "$calls" != *"/other/cluster"* && "$calls" == *"--kubeconfig ${work}/schedulable/runner-pod-kubeconfig."* ]] || fail "the caller's KUBECONFIG must not be used" "$calls"
[[ -z "$(find "${work}/schedulable" -name 'runner-pod-kubeconfig.*')" ]] || fail "the temporary kubeconfig must be removed"
! grep -rq "not-a-real-token" "${work}/schedulable/calls" || fail "the token must never reach a command line"

run cordoned unschedulable=true
[[ $status -ne 0 && "$output" == *"::error::Node node-a is cordoned"* && "$output" == *"Nothing was deployed"* ]] || fail "a cordoned node must stop the job before deploying" "$output"

run forbidden label-forbidden=1
[[ $status -eq 0 && "$output" == *"::warning::Could not label runner pod"* ]] || fail "a runner that may not label pods must warn and continue" "$output"
[[ "$calls" != *" get node "* ]] || fail "nothing else is queried once labelling failed" "$calls"

run node-unreadable node-unreadable=1
[[ $status -eq 0 && "$output" == *"::warning::"*"node node-a could not be read"* ]] || fail "an unreadable node must warn and continue" "$output"

in_cluster="" run outside
[[ $status -eq 0 && -z "$calls" && "$output" == *"no runner pod to protect"* ]] || fail "outside a cluster nothing must be called" "$output$calls"

echo "Runner pod protection behavior passed."
