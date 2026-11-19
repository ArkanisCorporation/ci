#!/usr/bin/env bash
# Summary:
#   Stands in for `helm` during `aspire deploy` only, to give its `helm upgrade` a wait timeout.
# Remarks:
#   Aspire.Hosting.Kubernetes hardcodes `helm upgrade --install <release> <chart> --namespace <ns> --wait [-f ...]`
#   and offers no way to add arguments, so Helm's 5m default --timeout applies to every deployment (ARK-744).
#   This shim appends `--timeout` to `upgrade` invocations and passes every other invocation through untouched.
#   Delete it, and the PATH override in wf-deploy-k8s-aspire.yml, once Aspire exposes the Helm arguments.
#
#   It also records which release and namespace the upgrade really targets, plus that release's history just
#   before the upgrade, so helm-release-guard can still restore the right release when the AppHost names it
#   differently than the workflow assumed. Only the name, the namespace and the revision list are recorded:
#   never the arguments, which may point at values.
#
#   Environment:
#     ARKANIS_REAL_HELM            Absolute path of the real helm binary. Required.
#     ARKANIS_HELM_UPGRADE_TIMEOUT Helm duration appended as --timeout. Required for `upgrade`.
#     ARKANIS_HELM_SHIM_RECORD     Optional file receiving `release=` and `namespace=` lines.

set -euo pipefail

real_helm="${ARKANIS_REAL_HELM:?ARKANIS_REAL_HELM must point at the real helm binary}"

subcommand=""
release=""
namespace=""
has_timeout=false
expect_namespace=false
for arg in "$@"; do
  if [[ "$expect_namespace" == true ]]; then
    namespace="$arg"
    expect_namespace=false
    continue
  fi

  case "$arg" in
    --namespace | -n) expect_namespace=true ;;
    --namespace=*) namespace="${arg#--namespace=}" ;;
    --timeout | --timeout=*) has_timeout=true ;;
    -*) ;;
    *)
      # Aspire puts only boolean flags before the release name, so the first two bare words are the
      # subcommand and the release. Later bare words are flag values and are ignored.
      if [[ -z "$subcommand" ]]; then
        subcommand="$arg"
      elif [[ -z "$release" ]]; then
        release="$arg"
      fi
      ;;
  esac
done

if [[ "$subcommand" != "upgrade" ]]; then
  exec "$real_helm" "$@"
fi

if [[ -n "${ARKANIS_HELM_SHIM_RECORD:-}" && -n "$release" && -n "$namespace" ]]; then
  printf 'release=%s\nnamespace=%s\n' "$release" "$namespace" > "$ARKANIS_HELM_SHIM_RECORD"
  # A release that does not exist yet makes `helm history` fail; an empty file then means "first install".
  "$real_helm" history "$release" --namespace "$namespace" --max 256 --output json \
    > "${ARKANIS_HELM_SHIM_RECORD}.history.json" 2> /dev/null || : > "${ARKANIS_HELM_SHIM_RECORD}.history.json"
fi

if [[ "$has_timeout" == true ]]; then
  exec "$real_helm" "$@"
fi

timeout="${ARKANIS_HELM_UPGRADE_TIMEOUT:?ARKANIS_HELM_UPGRADE_TIMEOUT must hold the Helm wait timeout}"
echo "helm shim: appending --timeout ${timeout} to helm upgrade (Aspire sets none)." >&2
exec "$real_helm" "$@" --timeout "$timeout"
