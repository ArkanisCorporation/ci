# schemas/AGENTS.md

## Scope

Applies schemas under `schemas/**`.

## Rules

- JSON Schema drafts must be declared.
- Schema names are stable public contract.
- Use enums for policy-bound strings.
- Use `additionalProperties: false` unless extension point needed.
- Additive optional property -> minor.
- Required property addition -> major unless new schema version.
- Keep examples valid.
- Generate docs from schemas; do not hand-maintain duplicate tables.

## Versioning

Schema IDs include version:

```text
https://example.invalid/schemas/ci-artifact-manifest/v1/schema.json
```

New incompatible schema -> `v2`, keep old reader where feasible.
