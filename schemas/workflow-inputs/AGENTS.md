# schemas/workflow-inputs/AGENTS.md

## Scope

Workflow input/output schemas.

## Rules

- One schema per public workflow.
- Schema filename mirrors workflow: `wf-setup-dotnet.yml` -> `wf-setup-dotnet.schema.json`.
- Include `inputs`, `secrets`, `outputs`, `permissions`, `artifacts` sections.
- Defaults in schema must match YAML.
- Descriptions must be terse but complete.
- Breaking schema change requires major release note.
