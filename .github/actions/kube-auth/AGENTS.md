# kube-auth/AGENTS.md

## Scope

Kubernetes/cloud auth helper for deploy workflows.

## Rules

- Prefer OIDC cloud auth over stored kubeconfig.
- Store kubeconfig under `$RUNNER_TEMP`; delete on cleanup.
- Never upload kubeconfig.
- Validate context, namespace, cluster identity before deploy.
- Support dry-run auth check.
- Keep provider-specific logic isolated behind explicit inputs.
