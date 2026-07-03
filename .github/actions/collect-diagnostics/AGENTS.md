# collect-diagnostics/AGENTS.md

## Scope

Failure diagnostics helper.

## Rules

- Run under `if: always()`.
- Redact secrets before upload.
- Bound log size; split large logs.
- Collect tool versions, env summary sans secrets, workspace tree bounded depth.
- For K8s: events, describe, rollout, failed pod logs, rendered manifests.
- For .NET: TRX/JUnit, coverage, binlog, `dotnet --info`.
- For Docker: Buildx inspect, build metadata, cache config.
