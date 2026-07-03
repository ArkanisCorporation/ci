# Modern .NET Package Migration

Audience: migration agents.

## Evidence

Template.NET and Aspire package repositories already use runner inputs, newer action versions, pipeline quality checks, and split deployment concepts.
Their main remaining issue is release configuration that still allows `@semantic-release/exec`.

## Target

The target is a narrow package pipeline.
Verification proves source quality.
Release metadata decides version and tag.
NuGet publish packs and pushes from a standalone job.
Backpropagation runs only after a real release.

## Checklist

- Replace local setup action calls with `wf-setup-dotnet.yml` when behavior matches.
- Add `wf-release-semantic.yml` with `allow-exec-plugin: false`.
- Add `wf-publish-nuget.yml` gated on release output.
- Preserve `runs-on-json` and `runs-on-self-hosted` inputs.
- Preserve package symbols and deterministic build settings.
- Update examples and fixtures if a package workflow pattern changes.

## Rollback

Keep old release workflow disabled until package publish dry run passes.
Restore local setup action only if platform setup lacks a documented required behavior.
