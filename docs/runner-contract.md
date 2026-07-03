# Runner Contract

Audience: platform maintainers and infrastructure owners.

## Inputs

Every public workflow accepts `runs-on-json`.
This input is passed through `fromJSON()` into `runs-on`.

Every public workflow accepts `runs-on-self-hosted`.
This input is true when `runs-on-json` targets self-hosted runners.

GitHub-hosted example:

```yaml
with:
  runs-on-json: '["ubuntu-latest"]'
  runs-on-self-hosted: false
```

Self-hosted example:

```yaml
with:
  runs-on-json: '["self-hosted","linux","x64","arc","dotnet"]'
  runs-on-self-hosted: true
```

## Hosted Runners

Hosted runners may use image labels such as `ubuntu-latest`, `windows-latest`, or versioned images.
Consumers override hosted images through `runs-on-json`.
Hosted-only behavior must be gated with `if: ${{ !inputs.runs-on-self-hosted }}`.

## Self-Hosted Runners

Self-hosted runners must be cattle, not pets.
Workflow runs must not rely on persistent workspace state.
Workflow runs must not assume Docker, kube context, sudo, or preinstalled SDKs unless the runner label contract says so.

Self-hosted-only behavior must be gated with `if: inputs.runs-on-self-hosted`.
Self-hosted preflight should record disk, workspace, OS, arch, and required tool paths.

## Labels

| Label | Meaning |
|---|---|
| `self-hosted` | Non-GitHub-hosted runner. |
| `linux`, `windows`, `macos` | Operating system family. |
| `x64`, `arm64` | CPU architecture. |
| `arc` | Kubernetes Actions Runner Controller runner. |
| `dotnet` | .NET SDK setup supported. |
| `node` | Node setup supported. |
| `docker` | Docker and Buildx available. |
| `buildkit-remote` | Remote BuildKit endpoint reachable. |
| `k8s` | Kubernetes API reachable and kubectl/helm usable. |

## Remote BuildKit

`wf-build-container.yml` accepts `buildkit-endpoint`.
When set, the workflow uses Docker Buildx with the `remote` driver.
This is intended for self-hosted or ARC runners that should not run Docker-in-Docker.

## Kubernetes Access

`wf-deploy-k8s-aspire.yml` accepts an optional `KUBE_CONFIG` secret.
When omitted, the runner must already have a valid kube context.
This supports cluster-hosted runners where kube access is provided by the runner environment.

## Required Hygiene

- Write temp data under `$RUNNER_TEMP` or the repository `artifacts/` directory.
- Clean up credentials or kubeconfig files where a workflow creates them.
- Prefer exact cache keys.
- Avoid broad restore keys for untrusted triggers.
- Keep self-hosted runner versions current with GitHub runner policy.
