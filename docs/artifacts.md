# Artifacts

Audience: workflow implementers and consumers.

## Contract

Artifacts are workflow products.
They are not random logs.

Each artifact should have:

- name;
- kind;
- producer;
- retention;
- path list;
- digest or registry digest when available;
- consumer expectation.

## Naming

Use this pattern:

```text
{repo}-{component}-{kind}-{sha-or-version-or-run}
```

## Kinds

| Kind | Use |
|---|---|
| `diagnostics` | Build, test, format, coverage, and runner failure evidence. |
| `package` | NuGet package outputs. |
| `image-metadata` | Container digest and Buildx metadata. |
| `deploy-report` | Kubernetes deployment output and deploy manifest. |
| `release-notes` | Release summaries and changelog output. |

## Manifest

Schema: `schemas/artifact-manifest.schema.json`.
Workflows that produce artifacts write `artifacts/artifact-manifest.json`.
Release, publish, and deploy workflows fail closed when required artifacts are missing.

## Best-Effort and Required Uploads

Last verified: 2026-10-31.

An upload of diagnostics must never decide whether a build passes.
Artifact storage is a shared, finite quota: when it is exhausted, `actions/upload-artifact` fails with `Failed to CreateArtifact: Artifact storage quota has been hit`, and before ARK-693 that failed every job of every consumer even when the real work passed.

- **Best-effort** uploads set `continue-on-error: true` and run with `if: ${{ always() && inputs.upload-diagnostics }}`. A failed upload shows as a step annotation; the job result comes from the real work. Every workflow with a best-effort upload exposes `upload-diagnostics` (boolean, default `true`), so a consumer can stop uploading while the quota is exhausted.
- **Required** uploads are the ones another job downloads. They keep failing the job and ignore `upload-diagnostics`.
- `scripts/validate-workflows.cs` enforces both: an upload is best-effort unless it is listed as required there.

| Workflow | Upload | Class | Why |
|---|---|---|---|
| `wf-publish-nuget.yml` | NuGet package artifact | required | The publish jobs download it. |
| `wf-verify-publish-nuget.yml` | Package artifact | best-effort | Nothing downloads it; the pack action already fails when no `.nupkg` is produced. |
| `wf-deploy-k8s-aspire.yml` | Deploy report | best-effort | The deployment has already happened when the upload runs. |
| `wf-publish-container-dotnet.yml`, `wf-publish-container-generic.yml` | Image metadata | best-effort | The digest is a job output, not an artifact. |
| All other `wf-*.yml` uploads | Diagnostics or dry-run reports | best-effort | Diagnostics only. |

The step summary, `run-metadata.json`, and `artifact-manifest.json` are written before the upload, so the summary stays useful when the upload is rejected or switched off.

## Diagnostic Size

Whole-tree diagnostic uploads exclude `artifacts/**/bin/**` and `artifacts/**/obj/**` unless GitHub Actions debug logging sets `runner.debug` to `1`.
The .NET test workflow also excludes `artifacts/test-results/**` and `artifacts/coverage/**` from normal diagnostics uploads because coverage reports and TRX attachments can dominate Actions artifact storage.
Callers can set `upload-test-results` or `upload-coverage` to `true` when those directories are needed without enabling all debug output.
Enable GitHub Actions debug logging when those bulky test or coverage files are needed for a one-off investigation.
Generated-code diagnostics also ignore `bin/` and `obj/` Git pathspecs unless `runner.debug` is enabled.

## Retention

PR diagnostics should be short-lived.
Main branch diagnostics should be longer-lived.
Release evidence should be attached to durable release or registry objects where possible.

Artifact storage counts for the whole retention window, so retention is the main lever on the quota.
Default `artifact-retention-days`:

| Workflows | Default |
|---|---|
| Pull-request workflows: `wf-dotnet-format`, `wf-dotnet-test`, `wf-node-*`, `wf-rust-*`, `wf-setup-dotnet-generated-code`, `wf-verify-release-semantic`, and the `wf-verify-*` dry runs | 7 days |
| Main-only diagnostics: `wf-release-semantic`, `wf-publish-docs-openviking` | 14 days |
| Publish and deploy: `wf-publish-nuget`, `wf-publish-container-*`, `wf-deploy-k8s-aspire` | 30 days |

## Browser-Viewable Artifacts

Use non-zipped artifacts for single HTML, Markdown, image, or log files when direct browser viewing helps.
Use ordinary archived artifacts for directory diagnostics.
