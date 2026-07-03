# upload-ci-artifacts/AGENTS.md

## Scope

Artifact upload helper.

## Rules

- Apply retention from `policies/artifact-retention.yml`.
- Use `if-no-files-found: error` for release/publish outputs.
- Use `compression-level: 0` for precompressed/large binary payloads.
- Prefer non-zipped single-file artifacts for HTML/Markdown/image/log reports.
- Exclude hidden files unless allowlisted.
- Return artifact metadata for manifest generation.
