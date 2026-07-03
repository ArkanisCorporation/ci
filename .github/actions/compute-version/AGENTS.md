# compute-version/AGENTS.md

## Scope

Version computation for packages/images/releases.

## Rules

- Separate compute from publish.
- Output semver, prerelease, build metadata, git SHA, source ref.
- Do not push tags or modify repo.
- Fail on ambiguous version source.
- Support release-please/semantic-release interop via documented inputs.
- No time-based versions for release artifacts unless explicitly policy-approved.
