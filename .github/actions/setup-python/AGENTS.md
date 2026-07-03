# setup-python/AGENTS.md

## Scope

Python runtime/package setup.

## Rules

- Support explicit Python version, version-file, or matrix.
- Support pip, pipenv, poetry, uv only when tested.
- Cache by lockfile + Python version + OS/arch.
- Prefer isolated venv under workspace/temp.
- Emit `python -VV`, package-manager version, dependency export/freeze.
- Do not globally install tools on self-host runners.
