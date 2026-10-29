# openviking-docs-publish Specification

## Purpose
Publishes a caller repository's documentation into OpenViking as a read-only, cross-repository
discovery mirror, with Git remaining the source of truth.

## Requirements

### Requirement: Doc selection

The workflow SHALL publish exactly the files tracked at the checked-out commit that match the
newline-separated `include-paths` globs, using GitHub Actions `paths:` glob semantics (`**` matches
zero or more directories). The default `include-paths` SHALL be `README.md`, `AGENTS.md`,
`CLAUDE.md`, `docs/**/*.md`, `.claude/rules/**/*.md`, `.claude/context/**/*.md`,
`openspec/specs/**/*.md`, and MUST equal repo-standards' `OPENVIKING_DOC_GLOBS`. A glob that
matches nothing SHALL NOT fail the run.

#### Scenario: Default selection

- **WHEN** a caller with `README.md`, `docs/README.md`, `docs/adr/0001.md`, and `src/README.md` runs with default inputs
- **THEN** the published set is `README.md`, `docs/README.md`, and `docs/adr/0001.md`

#### Scenario: Untracked files are ignored

- **WHEN** a matching file exists in the workspace but is not tracked by Git
- **THEN** it is not published

#### Scenario: Some globs match nothing

- **WHEN** the caller has no `CLAUDE.md` and no `.claude/` directory
- **THEN** the run publishes the files the other globs match

### Requirement: Mirror layout and replacement

The workflow SHALL import the selected files as one archive to
`<target-root>/<repository name>`, preserving repository-relative paths, where the default
`target-root` is `viking://resources/arkanis/projects` and the repository name comes from the
event context, never from caller input. Each import SHALL replace the previous mirror, so a doc
removed from the repository is absent from the mirror after the next successful run.

#### Scenario: Paths are preserved

- **WHEN** `docs/adr/0001.md` is published for repository `Infrastructure`
- **THEN** it is stored at `viking://resources/arkanis/projects/Infrastructure/docs/adr/0001.md`

#### Scenario: Deleted doc disappears

- **WHEN** a previously published doc is deleted from the repository and the workflow runs again
- **THEN** the mirror no longer contains that doc

### Requirement: Retrieval metadata

Each import SHALL use `processing_mode` from the `processing-mode` input (default `semantic_and_vectors`)
and SHALL replace the mirror's tags with `repo=<repository name>` and `commit=<checked-out SHA>`.

#### Scenario: Tags identify the source

- **WHEN** a run for repository `ci` at commit `abc1234…` succeeds
- **THEN** the mirror's records carry tags `repo=ci` and `commit=abc1234…`

### Requirement: Publishing is limited to the default branch

The workflow SHALL import only when the event is not a pull request event and the ref is the
caller's default branch. Otherwise it SHALL fail without contacting OpenViking, unless `dry-run`
is true.

#### Scenario: Push to the default branch

- **WHEN** the workflow runs for a `push` to the caller's default branch
- **THEN** it imports the docs

#### Scenario: Pull request

- **WHEN** the workflow runs for a `pull_request` event with `dry-run` false
- **THEN** it fails with a message naming the restriction and makes no OpenViking request

#### Scenario: Dry run on a pull request

- **WHEN** the workflow runs for a `pull_request` event with `dry-run` true
- **THEN** it collects and archives the docs, reports the file list, and makes no OpenViking request

### Requirement: Runner-provided credentials

The workflow SHALL read the OpenViking URL and API key only from the runner environment
(`OPENVIKING_URL`, `OPENVIKING_API_KEY`) and SHALL accept no secret input. When either value is
absent and `dry-run` is false, the run SHALL fail before any request, naming the runner pool that
provides them.

#### Scenario: Wrong runner pool

- **WHEN** the workflow runs on a runner without `OPENVIKING_API_KEY` and `dry-run` is false
- **THEN** it fails before any OpenViking request and the message names `arkanis-runners`

### Requirement: Credential protection

The workflow SHALL mask the API key before any step uses it, SHALL NOT pass it on a process command
line, and SHALL keep it out of logs, outputs, artifacts, and the step summary.

#### Scenario: Key never printed

- **WHEN** any step fails while the key is set
- **THEN** logs, summary, and uploaded diagnostics contain no copy of the key

### Requirement: Empty doc set

When no tracked file matches `include-paths`, the workflow SHALL skip the import, leave any
existing mirror unchanged, report that nothing was published, and succeed.

#### Scenario: Nothing to publish

- **WHEN** no tracked file matches
- **THEN** the run succeeds, makes no import request, and the summary says no docs matched

### Requirement: Size limit

The workflow SHALL fail before uploading when the archive exceeds `max-archive-bytes`
(default 20 MiB), reporting the size and the largest files.

#### Scenario: Oversized archive

- **WHEN** the archive is larger than `max-archive-bytes`
- **THEN** the run fails without uploading and the summary lists the largest files

### Requirement: Serialized runs

Runs for the same repository and target SHALL NOT overlap; a newer pending run SHALL supersede an
older pending one, and a running import SHALL NOT be cancelled.

#### Scenario: Two merges in quick succession

- **WHEN** a second run for the same repository starts while the first is importing
- **THEN** the second waits for the first to finish, and the mirror ends at the newer commit

### Requirement: Run reporting

Every run SHALL write a step summary (runner kind, requested labels, published file count, target
URI, commit, outcome), `run-metadata.json`, and diagnostics uploaded even on failure, and SHALL
expose outputs `published` (`true` only after OpenViking accepted the import), `file-count`, and `target-uri`.
OpenViking error responses SHALL be reported with their HTTP status and error body, with the key
redacted.

#### Scenario: Import rejected

- **WHEN** OpenViking rejects the import with an error status
- **THEN** the run fails, `published` is `false`, and the summary shows the status and error body
