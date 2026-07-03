# docker-metadata/AGENTS.md

## Scope

OCI image metadata/tag/digest/SBOM/provenance prep.

## Rules

- Deploy output = digest, not tag.
- Tags: semver, branch, `sha-<short>`; `latest` only for default stable branch.
- Record labels: source repo, revision, version, created, licenses.
- Build args are non-secret only. Use BuildKit secrets for secrets.
- Support multi-platform metadata.
- Emit image ref, digest, annotations, SBOM/provenance paths.
