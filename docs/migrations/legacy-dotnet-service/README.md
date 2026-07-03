# Legacy .NET Service Migration

Audience: migration agents.

## Evidence

ArkanisBackend and ArkanisDiscordBot use older checkout/setup-dotnet actions and release jobs that combine Docker, semantic-release, and backpropagation.
Hosting.Extensions.1Password is newer in setup actions but still follows a similar release shape.

## Target

The target is a split pipeline.
Verification runs first.
Semantic-release computes and publishes release metadata.
Container publish consumes the release output.
Deployment consumes an image tag or digest.

## Checklist

- Replace old checkout/setup-dotnet majors with `wf-setup-dotnet.yml`.
- Replace Docker setup, login, and build-push steps with `wf-publish-container-dotnet.yml`.
- Pass semantic-release `new-version` to `version`.
- Pass semantic-release `new-tag` to `version-tag`.
- Remove old `dotnet-setversion` flags because the .NET container workflow stamps versions before Docker Buildx by default.
- Replace semantic-release job with `wf-release-semantic.yml`.
- Remove `@semantic-release/exec` from release configuration.
- Move release backpropagation into a separate job.
- Move deploy calls into deploy jobs with explicit environments.
- Set top-level `permissions: {}`.
- Grant `contents`, `packages`, `id-token`, `issues`, and `pull-requests` only where needed.
- Use `runs-on-json` and `runs-on-self-hosted`.

## Rollback

Keep old workflow files available during migration review.
Disable publish or deploy jobs first when validating release behavior.
Re-enable irreversible jobs only after dry runs prove version, tag, and image outputs.
