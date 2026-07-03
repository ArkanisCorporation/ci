# Mock CI Project Fixtures Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add runnable TypeScript and .NET fixture projects that exercise shared CI workflows on real test data.

**Architecture:** Mock projects live under `tests/fixtures/mock-projects` and local caller workflows live under `tests/fixtures/workflow-contract`.
The public `@v1` contract fixtures remain unchanged, while new local fixtures call `./.github/workflows/...` so repository workflow edits can be smoke-tested before release.
Python is explicitly deferred until a public Python workflow contract exists.

**Tech Stack:** GitHub Actions reusable workflows, pnpm, TypeScript, Node.js, .NET 10, xUnit, ASP.NET Core, Docker Buildx, nektos/act.

---

### Task 1: TypeScript pnpm Fixture

**Files:**
- Create: `tests/fixtures/mock-projects/typescript-pnpm/package.json`
- Create: `tests/fixtures/mock-projects/typescript-pnpm/tsconfig.json`
- Create: `tests/fixtures/mock-projects/typescript-pnpm/src/greeting.ts`
- Create: `tests/fixtures/mock-projects/typescript-pnpm/src/greeting.test.ts`
- Create: `tests/fixtures/workflow-contract/typescript-pnpm-local.yml`

- [ ] **Step 1: Write the behavior test first**

Create `tests/fixtures/mock-projects/typescript-pnpm/src/greeting.test.ts`.

```ts
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { formatGreeting } from "./greeting.js";

describe("formatGreeting", () => {
  it("trims the project name and formats the CI greeting", () => {
    assert.equal(formatGreeting("  arkanis ci  "), "Hello, Arkanis Ci!");
  });

  it("rejects blank project names", () => {
    assert.throws(() => formatGreeting("   "), /project name is required/);
  });
});
```

- [ ] **Step 2: Add package and compiler configuration**

Create `package.json` with `packageManager: "pnpm@10.0.0"` and scripts for `lint`, `test`, and `build`.
Create `tsconfig.json` with `module: "NodeNext"`, `target: "ES2022"`, `strict: true`, and `outDir: "dist"`.

- [ ] **Step 3: Run the test and verify RED**

Run:

```powershell
corepack enable
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm install --lockfile-only
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm test
```

Expected: the test fails because `src/greeting.ts` does not exist.

- [ ] **Step 4: Implement the minimal TypeScript module**

Create `src/greeting.ts`.

```ts
export function formatGreeting(projectName: string): string {
  const normalized = projectName.trim();
  if (normalized.length === 0) {
    throw new Error("project name is required");
  }

  const displayName = normalized
    .split(/\s+/)
    .map((part) => part[0]!.toUpperCase() + part.slice(1).toLowerCase())
    .join(" ");

  return `Hello, ${displayName}!`;
}
```

- [ ] **Step 5: Add local workflow caller**

Create `tests/fixtures/workflow-contract/typescript-pnpm-local.yml`.

```yaml
name: TypeScript pnpm local fixture

on:
  workflow_dispatch:

permissions: {}

jobs:
  node:
    # Expected: success.
    name: typescript-pnpm @ local
    uses: ./.github/workflows/wf-setup-node.yml
    permissions:
      contents: read
    with:
      runs-on: ubuntu-latest
      runs-on-self-hosted: false
      node-version: 24.x
      package-manager: pnpm
      package-manager-version: "10"
      working-directory: tests/fixtures/mock-projects/typescript-pnpm
      enable-cache: false
      upload-diagnostics: false
```

- [ ] **Step 6: Verify GREEN**

Run:

```powershell
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm install --frozen-lockfile --ignore-scripts
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm lint
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm test
corepack pnpm --dir tests/fixtures/mock-projects/typescript-pnpm build
```

Expected: lint, test, and build exit 0.

### Task 2: .NET NuGet Library Fixture

**Files:**
- Create: `tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx`
- Create: `tests/fixtures/mock-projects/dotnet-nuget-library/src/Mock.NuGet.Library/Mock.NuGet.Library.csproj`
- Create: `tests/fixtures/mock-projects/dotnet-nuget-library/src/Mock.NuGet.Library/TextSlugger.cs`
- Create: `tests/fixtures/mock-projects/dotnet-nuget-library/tests/Mock.NuGet.Library.Tests/Mock.NuGet.Library.Tests.csproj`
- Create: `tests/fixtures/mock-projects/dotnet-nuget-library/tests/Mock.NuGet.Library.Tests/TextSluggerTests.cs`
- Create: `tests/fixtures/workflow-contract/dotnet-nuget-library-local.yml`
- Create: `tests/fixtures/workflow-contract/dotnet-nuget-verify-local.yml`

- [ ] **Step 1: Write xUnit tests first**

Create `TextSluggerTests.cs` with tests for lowercasing, trimming, punctuation removal, and blank input rejection.

- [ ] **Step 2: Add .NET project files**

Create a .NET 10 class library project with package metadata and a .NET 10 xUnit test project referencing it.
Set `RestorePackagesWithLockFile` to true in both projects so `dotnet restore --locked-mode` has lock files to verify.

- [ ] **Step 3: Generate lock files and verify RED**

Run:

```powershell
dotnet restore tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx --use-lock-file
dotnet test tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx
```

Expected: tests fail because `TextSlugger` does not exist.

- [ ] **Step 4: Implement the library**

Create `TextSlugger.cs`.
The implementation lowercases text, converts whitespace and punctuation runs to a single hyphen, trims hyphens, and throws `ArgumentException` when input is blank.

- [ ] **Step 5: Add local workflow callers**

Create `dotnet-nuget-library-local.yml` for `wf-setup-dotnet.yml` with `coverage-pr-comment: false`.
Create `dotnet-nuget-verify-local.yml` for `wf-verify-publish-nuget.yml` with `version: 1.2.3-ci.1`, `enable-cache: false`, and `dotnet-setversion-working-directory` pointed at the library fixture.
Each caller includes a comment declaring expected success.

- [ ] **Step 6: Verify GREEN**

Run:

```powershell
dotnet restore tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx --locked-mode
dotnet format tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx --verify-no-changes --verbosity diagnostic --no-restore
dotnet test tests/fixtures/mock-projects/dotnet-nuget-library/Mock.NuGet.Library.slnx --configuration Release --no-restore
dotnet pack tests/fixtures/mock-projects/dotnet-nuget-library/src/Mock.NuGet.Library/Mock.NuGet.Library.csproj --configuration Release --no-restore --output tests/fixtures/mock-projects/dotnet-nuget-library/artifacts/nuget -p:PackageVersion=1.2.3-ci.1 -p:ContinuousIntegrationBuild=true --include-symbols -p:SymbolPackageFormat=snupkg --include-source
```

Expected: restore, format, test, and pack exit 0 and produce a `.nupkg`.

### Task 3: .NET Container App Fixture

**Files:**
- Create: `tests/fixtures/mock-projects/dotnet-container-app/Mock.Container.App.slnx`
- Create: `tests/fixtures/mock-projects/dotnet-container-app/src/Mock.Container.App/Mock.Container.App.csproj`
- Create: `tests/fixtures/mock-projects/dotnet-container-app/src/Mock.Container.App/Program.cs`
- Create: `tests/fixtures/mock-projects/dotnet-container-app/src/Mock.Container.App/Dockerfile`
- Create: `tests/fixtures/mock-projects/dotnet-container-app/.dockerignore`
- Create: `tests/fixtures/workflow-contract/dotnet-container-verify-local.yml`

- [ ] **Step 1: Add the minimal app project**

Create a .NET 10 ASP.NET Core minimal API project.
The app reads `APP_BASE_URL`, defaults it to `http://localhost:5085`, and exposes `/healthz` plus `/` endpoints.

- [ ] **Step 2: Add Dockerfile**

Use public .NET SDK and ASP.NET runtime images.
Expose port `5085`.
Set `ASPNETCORE_URLS=http://+:5085`.
Pass `VERSION` as a non-secret build arg and OCI label.
Create `.dockerignore` with `**/bin/`, `**/obj/`, and `artifacts/`.

- [ ] **Step 3: Add local workflow caller**

Create `dotnet-container-verify-local.yml` for `wf-verify-publish-container-dotnet.yml`.
Use `image: local/mock-container-app`, `version: 1.2.3-ci.1`, `version-tag: v1.2.3-ci.1`, `context` pointing at the container app fixture, and `dockerfile` pointing at the fixture Dockerfile.
The caller includes a comment declaring expected success.

- [ ] **Step 4: Verify app and Docker build**

Run:

```powershell
dotnet restore tests/fixtures/mock-projects/dotnet-container-app/Mock.Container.App.slnx --use-lock-file
dotnet publish tests/fixtures/mock-projects/dotnet-container-app/src/Mock.Container.App/Mock.Container.App.csproj --configuration Release --no-restore
docker build -f tests/fixtures/mock-projects/dotnet-container-app/src/Mock.Container.App/Dockerfile tests/fixtures/mock-projects/dotnet-container-app -t local/mock-container-app:1.2.3-ci.1 --build-arg VERSION=1.2.3-ci.1
```

Expected: restore, publish, and Docker build exit 0.

### Task 4: Documentation And Validation

**Files:**
- Modify: `README.md`
- Create: `tests/fixtures/mock-projects/README.md`

- [ ] **Step 1: Document fixture coverage**

Add a compact README under `tests/fixtures/mock-projects` listing each fixture path, the workflows it exercises, and the Python deferral.
Add a root README pointer from local validation or quick navigation to the mock project fixtures.

- [ ] **Step 2: Run repository validators**

Run:

```powershell
dotnet run --file scripts/validate-workflows.cs
```

Expected: exit 0.

- [ ] **Step 3: Run bounded act smoke test when possible**

Run:

```powershell
act workflow_dispatch -W tests/fixtures/workflow-contract/typescript-pnpm-local.yml -j node -P ubuntu-latest=ghcr.io/catthehacker/ubuntu:act-latest
```

Expected: exit 0 when Docker can pull and run the configured image.
