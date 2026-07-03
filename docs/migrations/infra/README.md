# Infrastructure Migration

Audience: migration agents.

## Evidence

Infrastructure workflows repeat checkout, Azure CLI setup, 1Password secret loading, OpenTofu setup, plan, apply, cleanup, and PR comment behavior.
They also represent higher-risk side effects than normal application verification.

## Target

The target is a trust-zone pipeline.
Lint and validation are low-trust.
Plan is controlled and reviewable.
Apply and cleanup are protected environment actions.
Scheduled production apply is explicit and auditable.

## Checklist

- Keep top-level `permissions: {}`.
- Scope `pull-requests: write` only to PR comment jobs.
- Scope secrets only to plan or apply jobs that need them.
- Use explicit environment names for apply and cleanup.
- Record runner network requirements.
- Keep cloud and 1Password auth out of generic setup.
- Prepare reusable OpenTofu workflow requirements in docs before extraction.

## Rollback

Migrate lint and plan first.
Keep apply and cleanup unchanged until plan parity is proven.
