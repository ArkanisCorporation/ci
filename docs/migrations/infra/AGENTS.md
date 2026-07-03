# infra/AGENTS.md

## Scope

Use these instructions for infrastructure repositories.
Known example is Infrastructure.

## Goal

Separate lint, detect, plan, apply, cleanup, and scheduled production apply into explicit trust zones.
Prepare the repository for future OpenTofu reusable workflows without hiding cloud side effects.

## Inspection Order

1. Read verify, deploy, cleanup, scheduled apply, lint, detect-env, and local action workflows.
2. Identify Azure, 1Password, Tailscale, OpenTofu, and PR comment behavior.
3. Record which jobs can run on fork pull requests and which require protected environments.

## Target Shape

- Keep lint and plan safe for pull requests without secrets where possible.
- Keep apply and cleanup behind protected environments.
- Keep 1Password and cloud credentials scoped to jobs that need them.
- Use platform runner contracts and documentation even before a dedicated OpenTofu workflow exists.
- Prefer future `wf-setup-opentofu` and `wf-plan-opentofu` once available.

## Rules

- Do not run apply, destroy, or cleanup from untrusted events.
- Do not expose plan output if it contains secret-shaped values.
- Do not move cloud auth into setup jobs.
- Do not collapse environment detection, plan, apply, and cleanup into one job.
- Do not assume hosted runners can reach private networks.

## Verification

- Run formatting and validation checks.
- Run plan in dry or read-only mode before apply migration.
- Verify PR comments contain no secrets.
- Verify production apply requires protected environment approval.
