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

## Retention

PR diagnostics should be short-lived.
Main branch diagnostics should be longer-lived.
Release evidence should be attached to durable release or registry objects where possible.

## Browser-Viewable Artifacts

Use non-zipped artifacts for single HTML, Markdown, image, or log files when direct browser viewing helps.
Use ordinary archived artifacts for directory diagnostics.
