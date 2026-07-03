# publish-summary/AGENTS.md

## Scope

GitHub Step Summary writer.

## Rules

- Summary must stand alone without logs.
- Include inputs digest, resolved versions, jobs, artifacts, digests, next actions.
- Markdown tables allowed; keep rows bounded.
- Never print secrets or secret-derived tokens.
- Link artifacts/releases/deployments when URLs available.
- Include failure diagnosis block on `failure()`.
