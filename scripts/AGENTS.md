# scripts/AGENTS.md

## Scope

Applies scripts under `scripts/**`.

## Rules

- Scripts validate/generate; no hidden repo mutation.
- Default mode read-only or writes generated docs with explicit command name.
- Prefer .NET file-based scripts run with `dotnet run --file`.
- Use `CliWrap` for native command execution.
- Set `#:property TargetFramework=net10.0` in file scripts.
- Support Linux and Windows through .NET APIs where practical.
- No curl-pipe-shell.
- Quote paths.
- Work from repo root or locate root reliably.
- Print actionable errors.
- Exit non-zero on validation failure.
