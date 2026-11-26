#!/usr/bin/env bash
# Summary:
#   Marks the runner pod of a deploy job as busy, so a node drain waits for the deployment instead of killing it.
# Remarks:
#   A deploy job on an in-cluster (ARC) runner is a pod like any other: a node drain evicts it. That happened in
#   the middle of `helm upgrade` (ARK-784): the nightly reboot drained the node, the runner received SIGTERM, and
#   the release was left in `pending-upgrade` with no report and no rollback until the next run recovered it.
#
#   This script labels its own pod with `arkanis.dev/deploy-in-progress=true`. The label is the whole contract;
#   the cluster decides what it means. Infrastructure gives it two consumers: kured's `--blocking-pod-selector`
#   (the node is not cordoned for a reboot while such a pod runs on it) and a PodDisruptionBudget (every other
#   eviction of such a pod is refused). Nothing removes the label: an ephemeral runner pod is deleted when its job
#   ends, with or without success, so the protection can never outlive the deployment.
#
#   Only the busy runner is marked, never the pool: an idle warm runner must stay evictable, or the node it sits on
#   would never reboot.
#
#   A drain can already be under way when the job starts: the node is cordoned first, then its pods are evicted.
#   Labelling now would lose that race, so a deployment on a cordoned node stops here, before anything is deployed.
#
#   It talks to the cluster the runner pod lives in, through the pod's own service account, and ignores
#   KUBECONFIG: the deployment may target a different cluster. Outside a cluster it does nothing. A runner whose
#   service account may not label pods gets a warning and deploys unprotected, as before.
#
#   Environment:
#     ARKANIS_RUNNER_SERVICE_ACCOUNT_DIR  Service account mount. Defaults to the Kubernetes standard path; tests override it.
#     ARKANIS_RUNNER_POD_NAME             Pod name. Defaults to the hostname, which is the pod name on ARC runners.

set -euo pipefail

label="arkanis.dev/deploy-in-progress"
account_dir="${ARKANIS_RUNNER_SERVICE_ACCOUNT_DIR:-/var/run/secrets/kubernetes.io/serviceaccount}"
pod="${ARKANIS_RUNNER_POD_NAME:-$(hostname)}"

if [[ -z "${KUBERNETES_SERVICE_HOST:-}" || ! -r "${account_dir}/token" || ! -r "${account_dir}/namespace" ]]; then
  echo "Runner is not a Kubernetes pod with a service account token; no runner pod to protect."
  exit 0
fi

namespace="$(< "${account_dir}/namespace")"

# A kubeconfig of its own, so the caller's KUBECONFIG (possibly another cluster) is never used. It references the
# token file instead of containing the token: nothing secret is written or passed on a command line.
kubeconfig="$(mktemp "${RUNNER_TEMP:-/tmp}/runner-pod-kubeconfig.XXXXXX")"
trap 'rm -f "$kubeconfig"' EXIT
cat > "$kubeconfig" <<KUBECONFIG
apiVersion: v1
kind: Config
clusters:
  - name: runner
    cluster:
      server: https://kubernetes.default.svc
      certificate-authority: ${account_dir}/ca.crt
users:
  - name: runner
    user:
      tokenFile: ${account_dir}/token
contexts:
  - name: runner
    context: {cluster: runner, user: runner, namespace: ${namespace}}
current-context: runner
KUBECONFIG

runner_kubectl() {
  kubectl --kubeconfig "$kubeconfig" --request-timeout=20s "$@"
}

if ! runner_kubectl label pod "$pod" --namespace "$namespace" "${label}=true" --overwrite; then
  echo "::warning::Could not label runner pod ${namespace}/${pod} with ${label}=true. This deployment is not protected against a node drain."
  exit 0
fi

node="$(runner_kubectl get pod "$pod" --namespace "$namespace" --output "jsonpath={.spec.nodeName}" || true)"
if [[ -z "$node" ]]; then
  echo "::warning::Runner pod ${namespace}/${pod} is labelled ${label}=true, but its node could not be read. A drain that already started is not detected."
  exit 0
fi

if ! unschedulable="$(runner_kubectl get node "$node" --output "jsonpath={.spec.unschedulable}")"; then
  echo "::warning::Runner pod ${namespace}/${pod} is labelled ${label}=true, but node ${node} could not be read. A drain that already started is not detected."
  exit 0
fi

if [[ "$unschedulable" == "true" ]]; then
  echo "::error::Node ${node} is cordoned: it is being drained, and this runner pod would be evicted in the middle of the deployment. Nothing was deployed. Run the job again; the next runner starts on another node."
  exit 1
fi

echo "Runner pod ${namespace}/${pod} on node ${node} is labelled ${label}=true: a node drain now waits for this job."
