## Purpose

Builds and publishes OCI images from any caller Dockerfile without language-specific version
stamping, passing the release version to the build as a Docker build argument.

## ADDED Requirements

### Requirement: No language-specific stamping

`wf-verify-publish-container-generic.yml` and `wf-publish-container-generic.yml` SHALL NOT modify
caller files, install language SDKs, or require .NET project files before Docker Buildx runs.

#### Scenario: Rust repository

- **WHEN** a repository with only a Cargo workspace and a Dockerfile calls the publish workflow
- **THEN** the image builds and pushes without any `.csproj` lookup

### Requirement: Release version as build argument

Both workflows SHALL pass `<version-build-arg>=<version>` (default name `VERSION`) to Docker
Buildx unless `build-args` already defines that name or `version-build-arg` is empty.

#### Scenario: Default build argument

- **WHEN** `version` is `1.2.3` and `build-args` is empty
- **THEN** BuildKit receives `VERSION=1.2.3`

#### Scenario: Renamed build argument

- **WHEN** `version-build-arg` is `APP_VERSION`
- **THEN** BuildKit receives `APP_VERSION=1.2.3` and no `VERSION` argument is added

#### Scenario: Caller override wins

- **WHEN** `build-args` contains `VERSION=custom`
- **THEN** the workflow does not add a second `VERSION` argument

### Requirement: Bare version input

`version` SHALL be required and SHALL be a bare version without a leading `v`; tag-form values go
to `version-tag`.

#### Scenario: Leading v rejected

- **WHEN** `version` is `v1.2.3`
- **THEN** the job fails before building

### Requirement: Publish tags and digest

`wf-publish-container-generic.yml` SHALL push `sha-<12-char sha>`, plus `version-tag`,
`version-channel`, `<channel>-latest` (when `channel-latest`), and `extra-tags` when set, and SHALL
output the pushed image digest.

#### Scenario: Release publish

- **WHEN** `version-tag` is `v1.2.3` and `extra-tags` is `latest`
- **THEN** the image is pushed as `sha-<sha>`, `v1.2.3`, and `latest`, and `digest` is set

### Requirement: Attestations

`wf-publish-container-generic.yml` SHALL attach buildx provenance (default `mode=max`) and SBOM
(default `true`) attestations to the pushed image; the verify workflow SHALL disable both.

#### Scenario: Default publish attestations

- **WHEN** a caller publishes with default inputs
- **THEN** the pushed image index carries provenance and SBOM attestation manifests

### Requirement: Publish trust boundary

The publish job SHALL bind `environment-name` (default `container`), serialize per environment,
registry, and image without cancelling, and request only `contents: read` and `packages: write`.
The verify job SHALL bind no environment, never push, and request only `contents: read`.

#### Scenario: Pull request verification

- **WHEN** a pull request calls the verify workflow
- **THEN** the image builds without registry login, push, or environment approval

### Requirement: Build cache and BuildKit selection

Both workflows SHALL keep the .NET container workflows' cache and BuildKit behaviour: generated
`type=gha` cache when `enable-cache` is true and no explicit cache is set, no cache export to a
remote or runner-advertised BuildKit, and `buildkit-endpoint` or self-hosted `BUILDKIT_HOST`
selection.

#### Scenario: Shared BuildKit on a self-hosted runner

- **WHEN** `runs-on-self-hosted` is true, `buildkit-endpoint` is empty, and the runner sets `BUILDKIT_HOST`
- **THEN** Buildx uses the remote driver and imports, but does not export, the generated cache
