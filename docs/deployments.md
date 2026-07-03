# Deployments

Audience: release manager + platform maintainer.

## Model

Build once. Deploy by digest.

```text
source -> build artifact/image digest -> attest/manifest -> environment approval -> deploy -> rollout report
```

## Kubernetes rules

- Use image digest, not mutable tag.
- Render manifests before apply.
- Upload rendered manifests.
- Use `helm upgrade --install --atomic --wait --timeout` where Helm applies.
- Capture rollout status.
- On failure collect events, describe, pod logs.
- Namespace explicit.
- Context explicit.
- Verification workflow supported without environment approval or cluster mutation.

## Auth

- Prefer OIDC cloud auth.
- Avoid stored kubeconfig.
- Store temporary kubeconfig under `$RUNNER_TEMP`.
- Delete credentials in cleanup.

## Concurrency

Concurrency group per app/env:

```yaml
concurrency:
  group: deploy-${{ inputs.app }}-${{ inputs.environment }}
  cancel-in-progress: false
  queue: max
```

Use environment protection for prod.

## Rollback

Workflow should output:

- Previous release/revision.
- New release/revision.
- Rollback command/reference.
- Failed resource names.
