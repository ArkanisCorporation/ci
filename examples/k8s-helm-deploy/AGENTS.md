# k8s-helm-deploy/AGENTS.md

## Scope

Example for Helm/Kubernetes deployment.

## Rules

- Use GitHub Environments.
- Use OIDC cloud auth placeholder.
- Render manifests before apply.
- Upload rendered manifests and rollout report.
- Use deploy concurrency per app/env.
