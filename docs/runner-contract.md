# Runner Contract

Audience: platform maintainers and infrastructure owners.

## Inputs

Every public workflow accepts `runs-on`.
This input selects one runner label and defaults to `ubuntu-latest`.

Every public workflow accepts `runs-on-json`.
This input is passed through `fromJSON()` into `runs-on` when set.
It overrides `runs-on`.

Every public workflow accepts `runs-on-self-hosted`.
This input is true when the effective runner selection targets self-hosted runners.

GitHub-hosted example:

```yaml
with:
  runs-on: ubuntu-latest
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
Consumers override hosted images through `runs-on`.
Consumers use `runs-on-json` when a runner requires multiple labels.
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

## Runner-Advertised Capabilities

Some capabilities belong to a runner rather than to a caller, so the runner advertises them through
its own environment instead of a workflow input. Workflows treat them as optimisations only: an
absent variable means the portable default, never a failure.

| Variable | Set by | Effect |
|---|---|---|
| `BUILDKIT_HOST` | daedalus workers (`ArkanisCorporation/ci-runners`, ARK-529) | A shared bounded BuildKit is reachable; the container workflows use it when `buildkit-endpoint` is empty. See *Remote BuildKit* below. |
| `ARKANIS_PERSISTENT_NUGET_PACKAGES=true` | daedalus workers (`ArkanisCorporation/ci-runners`, ARK-528) | NuGet's default global-packages folder (`~/.nuget/packages`) lives on disk that persists across jobs, so `setup-dotnet`, `dotnet-pack-nuget`, `wf-setup-dotnet-generated-code.yml` and the Aspire deploy workflows skip the NuGet `runs-on/cache` step. Ignored when `NUGET_PACKAGES` points elsewhere. |

Rules for reading one:

- **Only when `runs-on-self-hosted` is true.** A hosted runner never advertises these, and the input is how this platform gates self-hosted behaviour.
- **In a shell step,** because the runner's machine environment is not part of the `env` expression context. Later steps gate on that step's output.
- **Record the decision** in the job's `artifacts/meta/runner-contract.txt`, so a run says which path it took.
- **Never a caller input instead.** Every caller would have to know which runner pool has which capability, and the runner already does.
- **Never inferred from `runs-on-self-hosted` alone.** ARC runners are self-hosted too, but have neither of these: they start each job in a fresh pod, and gating on the input alone would make them restore cold or point at a builder that is not there.

## Remote BuildKit

`wf-verify-publish-container-dotnet.yml` and `wf-publish-container-dotnet.yml` accept `buildkit-endpoint`.
When set, the workflow uses Docker Buildx with the `remote` driver.
When empty and `runs-on-self-hosted` is true, the workflow uses the runner's own `BUILDKIT_HOST` environment variable if it is set (see *Runner-Advertised Capabilities* above).
A runner pool advertises a shared builder this way without every caller hard-coding its address (daedalus: a bounded `buildkitd` run by `ci-runners`).
Otherwise the workflow creates a local `docker-container` builder.
If an endpoint is selected but unreachable, Buildx setup fails; there is no fallback to a local builder.
This is intended for self-hosted or ARC runners that should not run Docker-in-Docker.
The runner must also support Bash and .NET SDK setup before Docker Buildx runs.

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
