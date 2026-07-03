# docs/AGENTS.md

## Scope

Applies documentation under `docs/**`.

## Style

- Answer-first, terse, technical.
- No marketing copy.
- Prefer tables/checklists/code snippets.
- Each doc names owner audience: consumer, platform maintainer, security reviewer, release manager.
- Every workflow doc includes: purpose, inputs, secrets, outputs, permissions, artifacts, runner reqs, examples, failure modes.
- Every policy doc includes: rationale, rule, exception path, validation.

## Source discipline

- Verify current GitHub/Microsoft/Docker/Kubernetes docs before changing platform semantics.
- Link primary sources in `docs/references.md`.
- Date-sensitive guidance needs “last verified” line.
- Discussions/issues can explain edge cases; docs/specs decide contracts.

## Schema/docs sync

- Inputs/outputs documented from `schemas/workflow-inputs/*.schema.json`.
- If schema changes, update workflow catalog + examples + tests.
- No undocumented input, output, secret, permission.

## ADR rules

Create ADR for:

- New public workflow.
- Breaking API change.
- Security policy change.
- Runner model change.
- Artifact contract change.
- Release/publish/deploy strategy change.

ADR structure:

```text
# ADR-NNNN: Title
Status: proposed|accepted|deprecated|replaced
Context
Decision
Consequences
Migration
References
```

## Troubleshooting rules

Every failure mode entry has:

- Symptom.
- Likely cause.
- How to confirm.
- Fix.
- Prevention.
