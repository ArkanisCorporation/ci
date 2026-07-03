# detect-changes/AGENTS.md

## Scope

Path/project change detection.

## Rules

- Output JSON matrix, not shell fragments.
- Treat paths from Git as untrusted text; JSON encode, quote in shell.
- Never rely solely on workflow `on.paths` for required checks.
- Include `force-all` path for manual/scheduled full CI.
- Output stable fields: `project`, `path`, `language`, `test`, `pack`, `image`, `deploy`.
- Handle rename/delete, docs-only, root config changes, generated files.
