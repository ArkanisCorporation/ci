# ADR-0002: Container Publish Version Stamping

Status: accepted

## Context

Container images can be tagged with semantic-release tags while assemblies inside the image remain unstamped.
This happens when Dockerfiles run `dotnet publish` in a fresh checkout without receiving the bare semantic-release version.
MSBuild `Version` and `dotnet-setversion` require a bare semantic version such as `1.2.3`.
Docker and deployment tags may still use release tag values such as `v1.2.3`.

## Decision

Rename the .NET container workflow to `wf-publish-container-dotnet.yml`.
The workflow name now reflects that registry writes are a publishing concern and that the workflow is scoped to .NET images.
Add `.github/actions/dotnet-setversion/action.yml` as the reusable step bundle for .NET project stamping.
Expose `version` as the bare semantic version input.
Keep `version-tag` as the Docker image tag input.
Stamp .NET projects in every run before Docker Buildx.
The publish workflow checks out this CI platform source, runs the composite action, then removes the checkout before Docker Buildx.
The publish workflow adds `VERSION=<version>` to Docker build args unless the caller already provided `VERSION`.
Use `extra-tags` for extra mutable tags such as `latest`.
Use `channel-latest` only for `<channel>-latest`.

## Consequences

.NET image publishers can stamp assemblies before Docker Buildx runs.
Generic image publishers should use a future non-.NET workflow instead of this scoped workflow.
Consumers must migrate from `wf-build-container.yml` or `wf-publish-container.yml` to `wf-publish-container-dotnet.yml`.
Consumers must pass `needs.release.outputs.new-version` to `version`.
Consumers must pass `needs.release.outputs.new-tag` to `version-tag`.
Consumers must not pass secrets through `build-args`.

## Migration

Replace reusable workflow references from `wf-build-container.yml@v1` or `wf-publish-container.yml@v1` to `wf-publish-container-dotnet.yml@v1`.
Remove the `dotnet-setversion` input.
For release images, pass the bare release output to `version`.
For image tags, pass the tagged release output to `version-tag`.
Use `extra-tags: latest` when a stable release should also publish `latest`.
Validate changed workflows with `actionlint`, platform validation, and a bounded `act` smoke test where possible.

## References

- GitHub composite actions: https://docs.github.com/en/actions/tutorials/create-actions/create-a-composite-action
- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- Docker build-push action: https://github.com/docker/build-push-action
- dotnet tool install: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install
- dotnet-setversion: https://www.nuget.org/packages/dotnet-setversion
