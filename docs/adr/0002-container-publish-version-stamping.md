# ADR-0002: Container Publish Version Stamping

Status: accepted

## Context

Container images can be tagged with semantic-release tags while assemblies inside the image remain unstamped.
This happens when Dockerfiles run `dotnet publish` in a fresh checkout without receiving the bare semantic-release version.
MSBuild `Version` and `dotnet-setversion` require a bare semantic version such as `1.2.3`.
Docker and deployment tags may still use release tag values such as `v1.2.3`.

## Decision

Rename `wf-build-container.yml` to `wf-publish-container.yml`.
The workflow name now reflects that registry writes are a publishing concern.
Add `.github/actions/dotnet-setversion/action.yml` as the reusable step bundle for .NET project stamping.
Expose `version` as the bare semantic version input.
Keep `version-tag` as the Docker image tag input.
Expose `dotnet-setversion` as an opt-in flag because not every container image is a .NET image.
When `dotnet-setversion` is enabled, the publish workflow checks out this CI platform source, runs the composite action, then removes the checkout before Docker Buildx.
When `version` is set, the publish workflow adds `VERSION=<version>` to Docker build args unless the caller already provided `VERSION`.

## Consequences

.NET image publishers can stamp assemblies before Docker Buildx runs.
Generic image publishers can continue using the workflow without installing .NET tools.
Consumers must migrate from `wf-build-container.yml` to `wf-publish-container.yml`.
Consumers must pass `needs.release.outputs.new-version` to `version`.
Consumers must pass `needs.release.outputs.new-tag` to `version-tag`.
Consumers must not pass secrets through `build-args`.

## Migration

Replace reusable workflow references from `wf-build-container.yml@v1` to `wf-publish-container.yml@v1`.
For .NET images, set `dotnet-setversion: true`.
For release images, pass the bare release output to `version`.
For image tags, pass the tagged release output to `version-tag`.
Validate changed workflows with `actionlint`, platform validation, and a bounded `act` smoke test where possible.

## References

- GitHub composite actions: https://docs.github.com/en/actions/tutorials/create-actions/create-a-composite-action
- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- Docker build-push action: https://github.com/docker/build-push-action
- dotnet tool install: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install
- dotnet-setversion: https://www.nuget.org/packages/dotnet-setversion
