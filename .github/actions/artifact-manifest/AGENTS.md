# artifact-manifest/AGENTS.md

## Scope

Artifact manifest generation/validation.

## Rules

- Conform to `schemas/artifact-manifest.schema.json`.
- Include producer, artifact name, kind, path, digest, retention, URL/id when available.
- Missing required release artifact -> fail.
- Optional diagnostic artifact missing -> warn.
- Large content stays in artifact; manifest stores refs/digests only.
