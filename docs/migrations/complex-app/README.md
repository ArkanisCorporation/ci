# Complex App Migration

Audience: migration agents.

## Evidence

CitizenId already shows the desired direction by splitting release, NuGet publish, container publish, backpropagation, and deploy workflows.
ArkanisOverlay still has release work that mixes signing, Docker, release metadata, backpropagation, and post-release scanning concerns.

## Target

The target is a lane-based pipeline.
Each language or product lane verifies independently.
JetBrains CleanupCode lanes use `wf-setup-dotnet-jetbrains.yml` when the source repository already gates Rider/ReSharper cleanup rules.
Release metadata only decides version and tag.
Publish lanes consume release outputs.
Container publish lanes pass `new-version` as the bare `version` and `new-tag` as `version-tag`.
For .NET images, container publish lanes use `wf-publish-container-dotnet.yml`, which stamps versions before Docker Buildx.
Deploy lanes consume package, image, or manifest outputs.
Signing and scan lanes remain explicit side-effect jobs.

## Checklist

- Build a job map grouped by trust zone.
- Use `.NET` and Node verification workflows for common format, lint, test, and build lanes.
- Use publish workflows for NuGet and container outputs.
- Keep Windows runner needs explicit.
- Keep self-hosted runner labels explicit.
- Add environment names to signing and deploy jobs.
- Preserve manual approval gates.
- Document every third-party upload side effect.

## Rollback

Migrate one lane at a time.
Keep old deploy and signing lanes disabled but available until platform lanes produce matching artifacts.
