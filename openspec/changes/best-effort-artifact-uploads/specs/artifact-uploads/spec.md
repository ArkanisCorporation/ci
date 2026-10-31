## Purpose

Defines which artifact uploads in the reusable workflows may fail a job and how callers control
diagnostics uploads, so artifact storage limits never decide whether a build passes.

## ADDED Requirements

### Requirement: Best-effort diagnostics uploads

Every `actions/upload-artifact` step in a public workflow that uploads diagnostics, metadata, or a
report that no other job downloads SHALL set `continue-on-error: true`, so a failed upload does not
change the job result.

#### Scenario: Artifact storage quota exhausted

- **WHEN** the real work of a job succeeds and the diagnostics upload fails with "Artifact storage
  quota has been hit"
- **THEN** the job concludes successfully and the upload step shows the failure as an annotation

#### Scenario: Real work fails

- **WHEN** a test fails and the diagnostics upload also fails
- **THEN** the job still fails because of the test

### Requirement: Caller control of diagnostics uploads

Every public workflow with a best-effort upload SHALL expose a boolean `upload-diagnostics` input
defaulting to `true` and SHALL skip the upload when it is `false`.

#### Scenario: Uploads switched off

- **WHEN** a caller passes `upload-diagnostics: false`
- **THEN** no best-effort artifact is uploaded and the step summary is still written

### Requirement: Required artifacts stay required

An upload whose artifact another job downloads SHALL NOT set `continue-on-error` and SHALL NOT be
gated by `upload-diagnostics`. Today this is only the NuGet package artifact of
`wf-publish-nuget.yml`.

#### Scenario: Package handoff fails

- **WHEN** the NuGet package upload in `wf-publish-nuget.yml` fails
- **THEN** the pack job fails and no publish job runs

### Requirement: Short-lived pull-request diagnostics

Workflows that run on every pull request SHALL default `artifact-retention-days` to at most 7.

#### Scenario: Default retention

- **WHEN** a caller does not set `artifact-retention-days` on `wf-rust-check.yml`
- **THEN** the diagnostics artifact expires after 7 days
