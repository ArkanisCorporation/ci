#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property EnableAotAnalyzer=false
#:package CliWrap@3.9.0
#:package YamlDotNet@18.1.0

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using YamlDotNet.Serialization;

/*
 * Summary:
 *   Validates the public GitHub Actions platform contract.
 *
 * Remarks:
 *   Run with `dotnet run --file scripts/validate-workflows.cs`.
 *   The script emits diagnostics only and does not modify repository files.
 *   Every non-relative `uses:` reference under `.github/**` must use a version tag.
 */

var repoRoot = FindRepositoryRoot();
Directory.SetCurrentDirectory(repoRoot);

const string ActionlintVersion = "1.7.12";

var failures = new List<string>();

ValidateWorkflowInputSchemas();
ValidateWorkflowInputsMatchSchemas();
ValidateWorkflows();
ValidateRunnerSelectionInputContract();
ValidateWorkflowCatalogDiagrams();
ValidateJobDisplayNameContract();
ValidateStepDisplayNameContract();
ValidateCompositeActions();
ValidateContainerPublishContract();
ValidateNuGetPublishContract();
ValidateNuGetCompositeActionsContract();
ValidateNuGetPackSymbolContract();
ValidateCoverageReportContract();
ValidateGeneratedCodeContract();
ValidateWorkflowLintContract();
ValidatePlatformSelftestContract();
ValidateReleaseBackpropagationContract();
ValidateDotNetJetBrainsContract();
ValidatePlatformActionSourceContext();
ValidateSplitVerificationWorkflowsContract();
ValidateAspireAppHostInputContract();
ValidateRepositoryPipelineContract();
ValidateLocalActContract();
await ValidateDotNetActionFileScriptsAnalyzerCleanAsync();
await ValidateGeneratedWorkflowDocsAsync();
await RunActionlintWhenAvailableAsync();

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Workflow validation failed with {failures.Count} issue(s).");
    return 1;
}

Console.WriteLine("Workflow validation passed.");
return 0;

void ValidateWorkflowInputSchemas()
{
    var schemaRoot = Path.Combine(repoRoot, "schemas", "workflow-inputs");
    if (!Directory.Exists(schemaRoot))
    {
        return;
    }

    foreach (var schema in Directory.EnumerateFiles(schemaRoot, "wf-*.schema.json").Order(StringComparer.Ordinal))
    {
        var workflowName = Path.GetFileName(schema).Replace(".schema.json", ".yml", StringComparison.Ordinal);
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", workflowName);
        if (!File.Exists(workflowPath))
        {
            AddFailure($"{schema}: workflow input schema must have matching public workflow {workflowPath}.");
        }
    }
}

void ValidateWorkflowCatalogDiagrams()
{
    var workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
    var catalogPath = Path.Combine(repoRoot, "docs", "workflow-catalog.md");

    if (!Directory.Exists(workflowRoot))
    {
        AddFailure($"{workflowRoot}: workflow directory is required for catalog diagram validation.");
        return;
    }

    if (!File.Exists(catalogPath))
    {
        AddFailure($"{catalogPath}: workflow catalog is required.");
        return;
    }

    var publicWorkflows = Directory.EnumerateFiles(workflowRoot, "wf-*.yml")
        .Select(Path.GetFileName)
        .Where(name => name is not null)
        .Select(name => name!)
        .Order(StringComparer.Ordinal)
        .ToArray();

    var expected = publicWorkflows.ToHashSet(StringComparer.Ordinal);
    var diagramCounts = publicWorkflows.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
    var catalogText = File.ReadAllText(catalogPath);
    var sectionHeadings = Regex.Matches(catalogText, @"(?m)^##\s+(?<title>.+?)\s*$")
        .Cast<Match>()
        .ToArray();

    for (var index = 0; index < sectionHeadings.Length; index++)
    {
        var heading = sectionHeadings[index];
        var nextStart = index + 1 < sectionHeadings.Length ? sectionHeadings[index + 1].Index : catalogText.Length;
        var sectionText = catalogText[heading.Index..nextStart];
        var mermaidCount = Regex.Matches(sectionText, @"(?m)^```mermaid\s*$").Count;
        if (mermaidCount == 0)
        {
            continue;
        }

        var workflowNames = Regex.Matches(sectionText, @"wf-[A-Za-z0-9-]+\.yml")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (workflowNames.Length != 1)
        {
            AddFailure($"{catalogPath}: section '{heading.Groups["title"].Value}' has {mermaidCount} Mermaid diagram(s) but references {workflowNames.Length} public workflow name(s): {string.Join(", ", workflowNames)}.");
            continue;
        }

        var workflowName = workflowNames[0];
        if (!expected.Contains(workflowName))
        {
            AddFailure($"{catalogPath}: section '{heading.Groups["title"].Value}' documents stale workflow {workflowName}.");
            continue;
        }

        diagramCounts[workflowName] += mermaidCount;
    }

    foreach (var workflowName in publicWorkflows)
    {
        var count = diagramCounts[workflowName];
        if (count == 0)
        {
            AddFailure($"{catalogPath}: missing Mermaid diagram for public workflow {workflowName}.");
        }
        else if (count > 1)
        {
            AddFailure($"{catalogPath}: public workflow {workflowName} must have exactly one Mermaid diagram, found {count}.");
        }
    }
}

void ValidateWorkflows()
{
    var workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
    if (!Directory.Exists(workflowRoot))
    {
        Console.WriteLine("No workflow YAML files yet.");
        return;
    }

    var workflowFiles = Directory.EnumerateFiles(workflowRoot)
        .Where(IsYamlFile)
        .Order(StringComparer.Ordinal)
        .ToArray();

    if (workflowFiles.Length == 0)
    {
        Console.WriteLine("No workflow YAML files yet.");
        return;
    }

    foreach (var file in workflowFiles)
    {
        var text = File.ReadAllText(file);
        var lines = File.ReadAllLines(file);
        var fileName = Path.GetFileName(file);

        if (fileName.StartsWith("wf-", StringComparison.Ordinal))
        {
            if (!Regex.IsMatch(text, @"(?m)^\s*workflow_call:\s*$"))
            {
                AddFailure($"{file}: public workflow files named wf-* must expose on.workflow_call.");
            }

            var schemaName = Path.ChangeExtension(fileName, ".schema.json");
            var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", schemaName);
            if (!File.Exists(schemaPath))
            {
                AddFailure($"{file}: missing schema {schemaPath}.");
            }
        }

        if (!Regex.IsMatch(text, @"(?m)^permissions:\s*\{\}\s*$"))
        {
            AddFailure($"{file}: top-level permissions must be exactly 'permissions: {{}}'.");
        }

        if (Regex.IsMatch(text, @"(?m)^\s*pull_request_target:\s*$"))
        {
            AddFailure($"{file}: pull_request_target is not allowed in platform workflows.");
        }

        ValidateUsesReferences(file, lines);
        ValidateCacheOptOutContract(file, text, lines, isWorkflow: true);
    }
}

void ValidateRunnerSelectionInputContract()
{
    const string EffectiveRunsOnJsonExpression = "${{ fromJSON(inputs.runs-on-json || format('[{0}]', toJSON(inputs.runs-on || 'ubuntu-latest'))) }}";
    var workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
    if (!Directory.Exists(workflowRoot))
    {
        return;
    }

    foreach (var workflowPath in Directory.EnumerateFiles(workflowRoot, "wf-*.yml").Order(StringComparer.Ordinal))
    {
        var workflowText = File.ReadAllText(workflowPath);
        var workflowInputs = ReadWorkflowInputs(workflowPath);
        foreach (var runnerInput in new[] { "runs-on", "runs-on-json", "runs-on-self-hosted" })
        {
            if (!workflowInputs.ContainsKey(runnerInput))
            {
                AddFailure($"{workflowPath}: public workflows must expose {runnerInput} input.");
            }
        }

        if (!workflowText.Contains($"runs-on: {EffectiveRunsOnJsonExpression}", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: job runs-on must collapse runs-on into runs-on-json with '{EffectiveRunsOnJsonExpression}'.");
        }

        if (!workflowText.Contains("inputs.runs-on-json || format('[{0}]', toJSON(inputs.runs-on || 'ubuntu-latest'))", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: workflow diagnostics must record the effective runner JSON selection.");
        }
    }
}

void ValidateCompositeActions()
{
    var actionRoot = Path.Combine(repoRoot, ".github", "actions");
    if (!Directory.Exists(actionRoot))
    {
        return;
    }

    foreach (var file in Directory.EnumerateFiles(actionRoot, "*", SearchOption.AllDirectories)
                 .Where(path => string.Equals(Path.GetFileName(path), "action.yml", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(Path.GetFileName(path), "action.yaml", StringComparison.OrdinalIgnoreCase))
                 .Order(StringComparer.Ordinal))
    {
        var text = File.ReadAllText(file);
        var lines = File.ReadAllLines(file);

        if (!Regex.IsMatch(text, @"(?m)^name:\s*.+")
            || !Regex.IsMatch(text, @"(?m)^description:\s*.+")
            || !Regex.IsMatch(text, @"(?m)^\s*using:\s*composite\s*$"))
        {
            AddFailure($"{file}: composite action must declare name, description, and runs.using: composite.");
        }

        for (var index = 0; index < lines.Length; index++)
        {
            if (!Regex.IsMatch(lines[index], @"^\s*run:\s*"))
            {
                continue;
            }

            var stepStart = index;
            while (stepStart > 0 && !Regex.IsMatch(lines[stepStart], @"^\s*-\s+name:\s*"))
            {
                stepStart--;
            }

            var stepEnd = lines.Length - 1;
            for (var lookAhead = index + 1; lookAhead < lines.Length; lookAhead++)
            {
                if (Regex.IsMatch(lines[lookAhead], @"^\s*-\s+name:\s*"))
                {
                    stepEnd = lookAhead - 1;
                    break;
                }
            }

            var hasShell = lines[stepStart..(stepEnd + 1)].Any(line => Regex.IsMatch(line, @"^\s*shell:\s*"));
            if (!hasShell)
            {
                AddFailure($"{file}:{index + 1}: composite run step must declare shell.");
            }
        }

        ValidateUsesReferences(file, lines);
        ValidateCacheOptOutContract(file, text, lines, isWorkflow: false);
    }
}

void ValidateContainerPublishContract()
{
    foreach (var oldWorkflowName in new[] { "wf-build-container.yml", "wf-publish-container.yml" })
    {
        var oldWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", oldWorkflowName);
        if (File.Exists(oldWorkflowPath))
        {
            AddFailure($"{oldWorkflowPath}: .NET container publishing uses wf-publish-container-dotnet.yml.");
        }
    }

    var oldSchemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-publish-container.schema.json");
    if (File.Exists(oldSchemaPath))
    {
        AddFailure($"{oldSchemaPath}: .NET container publishing schema must be wf-publish-container-dotnet.schema.json.");
    }

    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-publish-container-dotnet.yml");
    var verifyWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-verify-publish-container-dotnet.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-publish-container-dotnet.schema.json");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-setversion", "action.yml");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: publish container workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);

        if (!Regex.IsMatch(workflowText, @"(?m)^name:\s*wf-publish-container-dotnet\s*$"))
        {
            AddFailure($"{workflowPath}: workflow name must be wf-publish-container-dotnet.");
        }

        foreach (var forbiddenInput in new[]
                 {
                      "latest-on-stable",
                      "push",
                      "dry-run",
                     "dotnet-setversion",
                     "dotnet-version",
                     "dotnet-global-json-file",
                     "dotnet-setversion-working-directory",
                     "dotnet-setversion-recursive",
                     "dotnet-setversion-project",
                     "dotnet-setversion-tool-version"
                 })
        {
            if (WorkflowDefinesInput(workflowPath, forbiddenInput))
            {
                AddFailure($"{workflowPath}: .NET container publish workflow must not expose stale input {forbiddenInput}.");
            }
        }

        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-setversion", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET container publish workflow must stamp projects with dotnet-setversion before Docker Buildx.");
        }

        if (!Regex.IsMatch(workflowText, @"(?m)^\s*environment:\s*\$\{\{\s*inputs\.environment-name\s*\}\}\s*$"))
        {
            AddFailure($"{workflowPath}: .NET container publish workflow must bind the publish job to inputs.environment-name.");
        }

        if (!workflowText.Contains("push: true", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET container publish workflow must always push; use wf-verify-publish-container-dotnet.yml for build-only verification.");
        }

        if (workflowText.Contains("LATEST_ON_STABLE", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: latest-on-stable tag path must be removed; use channel-latest or extra-tags.");
        }
    }

    ValidateContainerBuildCacheContract(workflowPath);
    ValidateContainerBuildCacheContract(verifyWorkflowPath);

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: publish container workflow schema is required.");
    }

    if (!File.Exists(actionPath))
    {
        AddFailure($"{actionPath}: dotnet-setversion composite action is required.");
    }
    else
    {
        foreach (var requiredInput in new[] { "version", "working-directory", "recursive", "tool-version" })
        {
            if (!ActionDefinesInput(actionPath, requiredInput))
            {
                AddFailure($"{actionPath}: dotnet-setversion action must expose {requiredInput} input.");
            }
        }
    }
}

void ValidateDotNetJetBrainsContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-dotnet-format.yml");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-jetbrains-cleanupcode", "action.yml");
    var actionScriptPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-jetbrains-cleanupcode", "run-cleanupcode.cs");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: .NET format workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);
        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-jetbrains-cleanupcode", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET format workflow must use dotnet-jetbrains-cleanupcode action.");
        }

        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/setup-dotnet", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET format workflow must use setup-dotnet before cleanup verification.");
        }

        if (!WorkflowDefinesInput(workflowPath, "run-dotnet-format"))
        {
            AddFailure($"{workflowPath}: .NET format workflow must expose run-dotnet-format for optional dotnet format.");
        }
    }

    if (!File.Exists(actionPath))
    {
        AddFailure($"{actionPath}: dotnet-jetbrains-cleanupcode composite action is required.");
    }
    else
    {
        var actionText = File.ReadAllText(actionPath);
        foreach (var requiredInput in new[] { "solution", "profile", "exclude", "fail-on-diff", "restore-tools" })
        {
            if (!ActionDefinesInput(actionPath, requiredInput))
            {
                AddFailure($"{actionPath}: dotnet-jetbrains-cleanupcode action must expose {requiredInput} input.");
            }
        }

        if (!actionText.Contains("dotnet run --file", StringComparison.Ordinal))
        {
            AddFailure($"{actionPath}: dotnet-jetbrains-cleanupcode action must delegate command logic to a .NET file script.");
        }
    }

    if (!File.Exists(actionScriptPath))
    {
        AddFailure($"{actionScriptPath}: dotnet-jetbrains-cleanupcode .NET file script is required.");
    }
    else
    {
        var actionScriptText = File.ReadAllText(actionScriptPath);
        foreach (var requiredToken in new[] { "#:package CliWrap@", "cleanupcode", "git", "diff", "Applied changes", "::group::Applied CleanupCode changes" })
        {
            if (!actionScriptText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{actionScriptPath}: dotnet-jetbrains-cleanupcode script must contain {requiredToken}.");
            }
        }
    }
}

async Task ValidateDotNetActionFileScriptsAnalyzerCleanAsync()
{
    var dotnet = FindExecutableOnPath("dotnet");
    if (dotnet is null)
    {
        AddFailure("dotnet executable is required to analyzer-check .NET action file scripts.");
        return;
    }

    var actionsRoot = Path.Combine(repoRoot, ".github", "actions");
    if (!Directory.Exists(actionsRoot))
    {
        return;
    }

    foreach (var scriptPath in Directory.EnumerateFiles(actionsRoot, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "arkanis-ci-file-script-analyzers", Guid.NewGuid().ToString("N"));
        var relativeScriptPath = Path.GetRelativePath(actionsRoot, scriptPath);
        var copiedScriptPath = Path.Combine(tempRoot, ".ci", "arkanis-ci", ".github", "actions", relativeScriptPath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copiedScriptPath)!);
            File.Copy(scriptPath, copiedScriptPath);
            File.WriteAllText(
                Path.Combine(tempRoot, ".editorconfig"),
                """
                root = true

                [*.cs]
                dotnet_diagnostic.CA1305.severity = error
                """);

            var result = await Cli.Wrap(dotnet)
                .WithArguments(["run", "--file", copiedScriptPath, "--no-launch-profile"])
                .WithWorkingDirectory(tempRoot)
                .WithEnvironmentVariables(new Dictionary<string, string?>
                {
                    ["GITHUB_WORKSPACE"] = string.Empty,
                    ["RUNNER_TEMP"] = string.Empty,
                    ["GITHUB_OUTPUT"] = string.Empty,
                    ["GITHUB_STEP_SUMMARY"] = string.Empty
                })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            var output = result.StandardOutput + result.StandardError;
            if (Regex.IsMatch(output, @"\berror\s+(CA|CS)\d{4}\b", RegexOptions.CultureInvariant)
                || output.Contains("The build failed.", StringComparison.Ordinal))
            {
                AddFailure($"{scriptPath}: .NET action file scripts must compile when downstream repositories enable CA1305. {FirstCompilerDiagnostic(output)}");
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}

static string FirstCompilerDiagnostic(string output)
{
    foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
    {
        if (Regex.IsMatch(line, @"\berror\s+(CA|CS)\d{4}\b", RegexOptions.CultureInvariant))
        {
            return line.Trim();
        }
    }

    return output.ReplaceLineEndings(" ").Trim();
}

void ValidatePlatformActionSourceContext()
{
    var workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
    if (!Directory.Exists(workflowRoot))
    {
        return;
    }

    foreach (var workflowPath in Directory.EnumerateFiles(workflowRoot, "wf-*.yml").Order(StringComparer.Ordinal))
    {
        var workflowText = File.ReadAllText(workflowPath);
        if (!workflowText.Contains(".ci/arkanis-ci/.github/actions/", StringComparison.Ordinal))
        {
            continue;
        }

        if (workflowText.Contains("${{ github.workflow_ref", StringComparison.Ordinal)
            || workflowText.Contains("${{ github.workflow_sha", StringComparison.Ordinal)
            || workflowText.Contains("${{ job.workflow_ref", StringComparison.Ordinal)
            || workflowText.Contains("${{ job.workflow_sha", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: platform action source checkout must use the actionlint-compatible fromJSON(toJSON(job)) form, not direct workflow metadata properties.");
        }

        if (!workflowText.Contains("fromJSON(toJSON(job)).workflow_repository", StringComparison.Ordinal)
            || !workflowText.Contains("fromJSON(toJSON(job)).workflow_sha", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: platform action source checkout must resolve repository/ref from job workflow_repository and workflow_sha metadata.");
        }

        const string ActionlintReason = "fromJSON(toJSON(job)) is only there because our pinned actionlint:1.7.12 does not know the newer documented job.workflow_ref / job.workflow_sha fields.";
        if (!workflowText.Contains(ActionlintReason, StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: platform action source checkout must document why fromJSON(toJSON(job)) is used.");
        }

        var reasonCount = Regex.Matches(workflowText, Regex.Escape(ActionlintReason)).Count;
        var repositoryCount = Regex.Matches(workflowText, @"(?m)^\s*repository:\s*\$\{\{\s*fromJSON\(toJSON\(job\)\)\.workflow_repository\s*\}\}\s*$").Count;
        var shaCount = Regex.Matches(workflowText, @"(?m)^\s*ref:\s*\$\{\{\s*fromJSON\(toJSON\(job\)\)\.workflow_sha\s*\}\}\s*$").Count;
        if (reasonCount != repositoryCount || reasonCount != shaCount)
        {
            AddFailure($"{workflowPath}: each platform action source checkout must document the actionlint fromJSON(toJSON(job)) rationale at the checkout site.");
        }

        if (workflowText.Contains("rm -rf .ci/arkanis-ci", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: platform action checkout must remain available for GitHub Actions post-job cleanup; do not remove .ci/arkanis-ci during the job.");
        }
    }
}

void ValidateContainerBuildCacheContract(string workflowPath)
{
    if (!File.Exists(workflowPath))
    {
        return;
    }

    var workflowText = File.ReadAllText(workflowPath);
    var enableCacheInput = ReadWorkflowInputs(workflowPath).GetValueOrDefault("enable-cache");
    if (enableCacheInput is null)
    {
        AddFailure($"{workflowPath}: container workflow must expose enable-cache for generated BuildKit cache fallback.");
    }
    else if (!string.Equals(NormalizeWorkflowDefault(enableCacheInput), "boolean:true", StringComparison.Ordinal))
    {
        AddFailure($"{workflowPath}: container workflow enable-cache must default to true.");
    }

    foreach (var requiredToken in new[]
             {
                 "id: build-cache",
                 "type=gha,scope=${scope}",
                 "cache-from: ${{ steps.build-cache.outputs.cache_from }}",
                 "cache-to: ${{ steps.build-cache.outputs.cache_to }}",
                 "artifacts/meta/docker-cache.txt"
             })
    {
        if (!workflowText.Contains(requiredToken, StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: container workflow must resolve generated BuildKit cache through {requiredToken}.");
        }
    }

    foreach (var staleToken in new[]
             {
                 "cache-from: ${{ inputs.cache-from }}",
                 "cache-to: ${{ inputs.cache-to }}"
             })
    {
        if (workflowText.Contains(staleToken, StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: container workflow must pass resolved BuildKit cache outputs instead of {staleToken}.");
        }
    }
}

void ValidateWorkflowInputsMatchSchemas()
{
    var workflowRoot = Path.Combine(repoRoot, ".github", "workflows");
    var schemaRoot = Path.Combine(repoRoot, "schemas", "workflow-inputs");
    if (!Directory.Exists(workflowRoot) || !Directory.Exists(schemaRoot))
    {
        return;
    }

    foreach (var workflowPath in Directory.EnumerateFiles(workflowRoot, "wf-*.yml").Order(StringComparer.Ordinal))
    {
        var workflowName = Path.GetFileName(workflowPath);
        var schemaPath = Path.Combine(schemaRoot, Path.ChangeExtension(workflowName, ".schema.json"));
        if (!File.Exists(schemaPath))
        {
            continue;
        }

        var workflowInputs = ReadWorkflowInputs(workflowPath);
        var schemaInputs = ReadWorkflowInputSchema(schemaPath).Inputs;

        foreach (var inputName in workflowInputs.Keys.Except(schemaInputs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            AddFailure($"{workflowPath}: workflow input {inputName} is missing from {schemaPath}.");
        }

        foreach (var inputName in schemaInputs.Keys.Except(workflowInputs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            AddFailure($"{schemaPath}: schema input {inputName} is missing from {workflowPath}.");
        }

        foreach (var inputName in workflowInputs.Keys.Intersect(schemaInputs.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var workflowInput = workflowInputs[inputName];
            var schemaInput = schemaInputs[inputName];
            var workflowType = NormalizeWorkflowInputType(workflowInput.Type);

            if (!InputTypesMatch(workflowType, schemaInput.Type))
            {
                AddFailure($"{workflowPath}: input {inputName} type '{workflowInput.Type ?? "unspecified"}' must match schema type '{schemaInput.Type}'.");
            }

            if (workflowInput.Required != schemaInput.Required)
            {
                AddFailure($"{workflowPath}: input {inputName} required={workflowInput.Required.ToString().ToLowerInvariant()} must match schema required={schemaInput.Required.ToString().ToLowerInvariant()}.");
            }

            if (workflowInput.HasDefault != schemaInput.HasDefault)
            {
                AddFailure($"{workflowPath}: input {inputName} default presence must match {schemaPath}.");
                continue;
            }

            if (workflowInput.HasDefault
                && !string.Equals(NormalizeWorkflowDefault(workflowInput), schemaInput.DefaultValue, StringComparison.Ordinal))
            {
                AddFailure($"{workflowPath}: input {inputName} default '{FormatDisplayDefault(workflowInput.Default)}' must match schema default '{schemaInput.DisplayDefault}'.");
            }
        }
    }
}

void ValidateNuGetPublishContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-publish-nuget.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-publish-nuget.schema.json");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: NuGet publish workflow is required.");
        return;
    }

    var workflowText = File.ReadAllText(workflowPath);

    if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-pack-nuget", StringComparison.Ordinal))
    {
        AddFailure($"{workflowPath}: NuGet publish workflow must delegate package creation to dotnet-pack-nuget.");
    }

    if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-publish-nuget", StringComparison.Ordinal))
    {
        AddFailure($"{workflowPath}: NuGet publish workflow must delegate package pushes to dotnet-publish-nuget.");
    }

    if (WorkflowDefinesInput(workflowPath, "dry-run"))
    {
        AddFailure($"{workflowPath}: NuGet publish workflow must not expose dry-run; use wf-verify-publish-nuget.yml for package verification.");
    }

    if (!Regex.IsMatch(workflowText, @"(?m)^\s*environment:\s*\$\{\{\s*inputs\.environment-name\s*\}\}\s*$"))
    {
        AddFailure($"{workflowPath}: NuGet publish workflow must bind publish jobs to inputs.environment-name.");
    }

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: NuGet publish workflow schema is required.");
    }
}

void ValidateNuGetCompositeActionsContract()
{
    var packActionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-pack-nuget", "action.yml");
    var publishActionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-publish-nuget", "action.yml");
    var verifyWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-verify-publish-nuget.yml");

    if (!File.Exists(packActionPath))
    {
        AddFailure($"{packActionPath}: NuGet pack composite action is required for caller-owned Trusted Publishing workflows.");
    }
    else
    {
        var packActionText = File.ReadAllText(packActionPath);
        foreach (var requiredInput in new[] { "project", "version", "include-symbols", "include-source", "dotnet-setversion", "artifact-name" })
        {
            if (!ActionDefinesInput(packActionPath, requiredInput))
            {
                AddFailure($"{packActionPath}: dotnet-pack-nuget action must expose {requiredInput} input.");
            }
        }

        foreach (var requiredToken in new[] { "dotnet restore", "dotnet pack", "--include-source", "-p:SymbolPackageFormat=snupkg", "dotnet tool install dotnet-setversion" })
        {
            if (!packActionText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{packActionPath}: dotnet-pack-nuget action must contain {requiredToken}.");
            }
        }
    }

    if (!File.Exists(publishActionPath))
    {
        AddFailure($"{publishActionPath}: NuGet publish composite action is required for caller-owned Trusted Publishing workflows.");
    }
    else
    {
        var publishActionText = File.ReadAllText(publishActionPath);
        foreach (var requiredInput in new[] { "api-key", "source", "package-directory", "skip-duplicate" })
        {
            if (!ActionDefinesInput(publishActionPath, requiredInput))
            {
                AddFailure($"{publishActionPath}: dotnet-publish-nuget action must expose {requiredInput} input.");
            }
        }

        foreach (var requiredToken in new[] { "::add-mask::", "dotnet nuget push", "--api-key", "--skip-duplicate" })
        {
            if (!publishActionText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{publishActionPath}: dotnet-publish-nuget action must contain {requiredToken}.");
            }
        }

        if (publishActionText.Contains("NuGet/login", StringComparison.Ordinal))
        {
            AddFailure($"{publishActionPath}: dotnet-publish-nuget must consume an API key from the caller and must not request OIDC itself.");
        }
    }

    if (File.Exists(verifyWorkflowPath))
    {
        var verifyWorkflowText = File.ReadAllText(verifyWorkflowPath);
        if (!verifyWorkflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-pack-nuget", StringComparison.Ordinal))
        {
            AddFailure($"{verifyWorkflowPath}: NuGet verification workflow must delegate package creation to dotnet-pack-nuget.");
        }
    }
}

void ValidateNuGetPackSymbolContract()
{
    foreach (var relativePath in new[]
             {
                 Path.Combine(".github", "actions", "dotnet-pack-nuget", "action.yml"),
                 Path.Combine(".github", "workflows", "wf-verify-publish-nuget.yml"),
                 Path.Combine(".github", "workflows", "wf-publish-nuget.yml")
             })
    {
        var contractPath = Path.Combine(repoRoot, relativePath);
        if (!File.Exists(contractPath))
        {
            continue;
        }

        var contractText = File.ReadAllText(contractPath);
        if (contractText.Contains("--symbol-package-format", StringComparison.Ordinal))
        {
            AddFailure($"{contractPath}: dotnet pack must set SymbolPackageFormat with -p:SymbolPackageFormat=snupkg, not unsupported CLI switch --symbol-package-format.");
        }

        if (relativePath.Contains($"dotnet-pack-nuget{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !contractText.Contains("-p:SymbolPackageFormat=snupkg", StringComparison.Ordinal))
        {
            AddFailure($"{contractPath}: NuGet symbol packages must pass -p:SymbolPackageFormat=snupkg to dotnet pack.");
        }
    }
}

void ValidateSplitVerificationWorkflowsContract()
{
    var expectedVerifyWorkflows = new[]
    {
        "wf-verify-release-semantic.yml",
        "wf-verify-publish-nuget.yml",
        "wf-verify-publish-container-dotnet.yml",
        "wf-verify-deploy-k8s-aspire.yml",
    };

    foreach (var workflowName in expectedVerifyWorkflows)
    {
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", workflowName);
        var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", Path.ChangeExtension(workflowName, ".schema.json"));
        if (!File.Exists(workflowPath))
        {
            AddFailure($"{workflowPath}: verification workflow is required for dry-run/no-environment validation.");
            continue;
        }

        if (!File.Exists(schemaPath))
        {
            AddFailure($"{schemaPath}: verification workflow schema is required.");
        }

        var workflowText = File.ReadAllText(workflowPath);
        var normalizedWorkflowText = NormalizeLineEndings(workflowText);
        if (Regex.IsMatch(workflowText, @"(?m)^\s*environment:\s*"))
        {
            AddFailure($"{workflowPath}: verification workflows must not bind GitHub environments.");
        }

        if (workflowName == "wf-verify-release-semantic.yml"
            && !normalizedWorkflowText.Contains("permissions:\n      contents: write", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: semantic-release dry-run verification must request contents: write because semantic-release verifies tag push access.");
        }

        if (Regex.IsMatch(workflowText, @"(?m)^\s*dry-run:\s*$"))
        {
            AddFailure($"{workflowPath}: verification workflows must not expose dry-run; verification is the only mode.");
        }
    }

    var noDryRunWorkflows = new[]
    {
        "wf-release-semantic.yml",
        "wf-publish-nuget.yml",
        "wf-deploy-k8s-aspire.yml",
    };

    foreach (var workflowName in noDryRunWorkflows)
    {
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", workflowName);
        if (!File.Exists(workflowPath))
        {
            continue;
        }

        if (WorkflowDefinesInput(workflowPath, "dry-run"))
        {
            AddFailure($"{workflowPath}: production workflow must not expose dry-run; use the matching wf-verify-* workflow.");
        }
    }
}

void ValidateCoverageReportContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-dotnet-test.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-dotnet-test.schema.json");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-coverage-report", "action.yml");
    var scriptPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-coverage-report", "run-coverage-report.cs");

    if (File.Exists(workflowPath))
    {
        var workflowText = File.ReadAllText(workflowPath);
        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-coverage-report", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET test workflow must use dotnet-coverage-report action when enabled.");
        }
    }

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: .NET test workflow schema is required.");
    }

    if (!File.Exists(actionPath))
    {
        AddFailure($"{actionPath}: dotnet-coverage-report composite action is required.");
    }
    else
    {
        var actionText = File.ReadAllText(actionPath);
        if (!actionText.Contains("dotnet run --file", StringComparison.Ordinal))
        {
            AddFailure($"{actionPath}: dotnet-coverage-report action must delegate command logic to a .NET file script.");
        }
    }

    if (!File.Exists(scriptPath))
    {
        AddFailure($"{scriptPath}: dotnet-coverage-report .NET file script is required.");
    }
    else
    {
        var scriptText = File.ReadAllText(scriptPath);
        foreach (var requiredToken in new[] { "#:package CliWrap@", "reportgenerator", "gh", "pr", "comment" })
        {
            if (!scriptText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{scriptPath}: dotnet-coverage-report script must contain {requiredToken}.");
            }
        }

        if (scriptText.Contains("--allow-roll-forward", StringComparison.Ordinal))
        {
            AddFailure($"{scriptPath}: dotnet-coverage-report must not install ReportGenerator with --allow-roll-forward because ReportGenerator runtimeconfig already uses legacy roll-forward settings.");
        }
    }
}

void ValidateGeneratedCodeContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-setup-dotnet-generated-code.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-setup-dotnet-generated-code.schema.json");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-generated-code-diff", "action.yml");
    var scriptPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-generated-code-diff", "run-generated-code-diff.cs");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: .NET generated code workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);
        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-generated-code-diff", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: .NET generated code workflow must use dotnet-generated-code-diff action.");
        }
    }

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: .NET generated code workflow schema is required.");
    }

    if (!File.Exists(actionPath))
    {
        AddFailure($"{actionPath}: dotnet-generated-code-diff composite action is required.");
    }
    else if (!File.ReadAllText(actionPath).Contains("dotnet run --file", StringComparison.Ordinal))
    {
        AddFailure($"{actionPath}: dotnet-generated-code-diff action must delegate command logic to a .NET file script.");
    }

    if (!File.Exists(scriptPath))
    {
        AddFailure($"{scriptPath}: dotnet-generated-code-diff .NET file script is required.");
    }
    else
    {
        var scriptText = File.ReadAllText(scriptPath);
        foreach (var requiredToken in new[] { "#:package CliWrap@", "git", "diff", "ls-files" })
        {
            if (!scriptText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{scriptPath}: dotnet-generated-code-diff script must contain {requiredToken}.");
            }
        }
    }
}

void ValidateWorkflowLintContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-lint-github-actions.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-lint-github-actions.schema.json");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: GitHub Actions lint workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);
        foreach (var requiredToken in new[] { "raven-actions/actionlint@v2", "runs-on-self-hosted", "enable-cache" })
        {
            if (!workflowText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{workflowPath}: GitHub Actions lint workflow must contain {requiredToken}.");
            }
        }

        foreach (var requiredReportingToken in new[] { "Set up Python 3.14 + pipx", "python --version", "pipx --version", "| Python |", "| pipx |" })
        {
            if (!workflowText.Contains(requiredReportingToken, StringComparison.Ordinal))
            {
                AddFailure($"{workflowPath}: GitHub Actions lint workflow must report {requiredReportingToken} after setting up Python and pipx.");
            }
        }
    }

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: GitHub Actions lint workflow schema is required.");
    }
}

void ValidatePlatformSelftestContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-platform-selftest.yml");
    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: platform selftest workflow is required.");
        return;
    }

    var workflowText = File.ReadAllText(workflowPath);
    var actionlintIndex = workflowText.IndexOf("uses: raven-actions/actionlint@v2", StringComparison.Ordinal);
    var validatorIndex = workflowText.IndexOf("dotnet run --file scripts/validate-workflows.cs", StringComparison.Ordinal);

    if (actionlintIndex < 0)
    {
        AddFailure($"{workflowPath}: platform selftest must run raven-actions/actionlint@v2 before the contract validator.");
    }
    else if (validatorIndex >= 0 && actionlintIndex > validatorIndex)
    {
        AddFailure($"{workflowPath}: platform selftest actionlint step must run before scripts/validate-workflows.cs.");
    }

    if (!Regex.IsMatch(workflowText, $@"(?m)^\s*version:\s*{Regex.Escape(ActionlintVersion)}\s*$"))
    {
        AddFailure($"{workflowPath}: platform selftest actionlint version must be pinned to {ActionlintVersion}.");
    }

    if (!Regex.IsMatch(workflowText, @"(?m)^\s*flags:\s*-color\s*$"))
    {
        AddFailure($"{workflowPath}: platform selftest actionlint step must pass '-color' for consistent diagnostics.");
    }
}

void ValidateJobDisplayNameContract()
{
    var expectedWorkflowJobNames = new[]
    {
        ("wf-release-semantic.yml", "    name: ${{ github.head_ref || github.ref_name }} @ ${{ inputs.environment-name }}"),
        ("wf-release-backpropagation.yml", "    name: ${{ inputs.new-version }} @ ${{ inputs.default-branch }}"),
        ("wf-publish-nuget.yml", "    name: ${{ inputs.version }} @ ${{ inputs.environment-name }}"),
        ("wf-publish-container-dotnet.yml", "    name: ${{ inputs.version || inputs.version-tag }} @ ${{ inputs.environment-name }}"),
        ("wf-deploy-k8s-aspire.yml", "    name: ${{ inputs.image-tag || inputs.kubernetes-namespace }} @ ${{ inputs.environment-name }}"),
        ("wf-verify-release-semantic.yml", "    name: ${{ github.head_ref || github.ref_name }}"),
        ("wf-verify-publish-nuget.yml", "    name: ${{ inputs.version }}"),
        ("wf-verify-publish-container-dotnet.yml", "    name: ${{ inputs.version || inputs.version-tag }}"),
        ("wf-verify-deploy-k8s-aspire.yml", "    name: ${{ inputs.image-tag || inputs.kubernetes-namespace }}"),
        ("wf-node-lint.yml", "    name: ${{ inputs.working-directory }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-node-test.yml", "    name: ${{ inputs.working-directory }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-node-build.yml", "    name: ${{ inputs.working-directory }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-dotnet-format.yml", "    name: ${{ inputs.solution }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-dotnet-test.yml", "    name: ${{ inputs.solution }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-setup-dotnet-generated-code.yml", "    name: ${{ inputs.solution }} @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-lint-github-actions.yml", "    name: workflows @ ${{ github.head_ref || github.ref_name }}"),
        ("wf-platform-selftest.yml", "    name: contracts @ ${{ github.head_ref || github.ref_name }}"),
    };

    foreach (var (fileName, expectedNameLine) in expectedWorkflowJobNames)
    {
        var path = Path.Combine(repoRoot, ".github", "workflows", fileName);
        if (!File.Exists(path))
        {
            AddFailure($"{path}: workflow file is required for job display name contract.");
            continue;
        }

        if (!File.ReadAllText(path).Contains(expectedNameLine, StringComparison.Ordinal))
        {
            AddFailure($"{path}: job display name must be '{expectedNameLine.Trim()}'.");
        }
    }

    var releaseWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml");
    if (!File.Exists(releaseWorkflowPath))
    {
        AddFailure($"{releaseWorkflowPath}: release workflow is required for caller display name contract.");
    }
    else
    {
        var releaseText = File.ReadAllText(releaseWorkflowPath);
        foreach (var expectedNameLine in new[]
                 {
                     "    name: platform @ ${{ github.head_ref || github.ref_name }}",
                     "    name: typescript-pnpm lint @ ${{ github.head_ref || github.ref_name }}",
                     "    name: typescript-pnpm test @ ${{ github.head_ref || github.ref_name }}",
                     "    name: typescript-pnpm build @ ${{ github.head_ref || github.ref_name }}",
                     "    name: dotnet-nuget-library format @ ${{ github.head_ref || github.ref_name }}",
                     "    name: dotnet-nuget-library test @ ${{ github.head_ref || github.ref_name }}",
                     "    name: dotnet-nuget-verify @ ${{ github.head_ref || github.ref_name }}",
                     "    name: dotnet-container-verify @ ${{ github.head_ref || github.ref_name }}",
                     "    name: repo",
                     "    name: repo @ publish",
                 })
        {
            if (!releaseText.Contains(expectedNameLine, StringComparison.Ordinal))
            {
                AddFailure($"{releaseWorkflowPath}: caller display name must be '{expectedNameLine.Trim()}'.");
            }
        }
    }
}

void ValidateStepDisplayNameContract()
{
    var expectedWorkflowStepNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["wf-release-semantic.yml"] =
        [
            "      - name: Semantic Release @ ${{ inputs.semantic-release-version }} to ${{ inputs.environment-name }}",
            "      - name: Write release diagnostics @ ${{ steps.semantic-release.outputs.new_release_git_tag || github.ref_name }}",
        ],
        ["wf-verify-release-semantic.yml"] =
        [
            "      - name: Semantic Release (Dry Run) @ ${{ inputs.semantic-release-version }}",
            "      - name: Write release diagnostics @ ${{ steps.semantic-release.outputs.new_release_git_tag || github.ref_name }}",
        ],
        ["wf-publish-nuget.yml"] =
        [
            "      - name: Download package artifacts @ ${{ needs.pack.outputs.package-artifact-name }}",
            "      - name: Publish packages via Trusted Publishing @ ${{ inputs.source }}",
            "      - name: Publish packages via API key @ ${{ inputs.source }}",
        ],
        ["wf-publish-container-dotnet.yml"] =
        [
            "      - name: Publish image ${{ inputs.image }} @ ${{ inputs.platforms }}",
            "      - name: Write image metadata @ ${{ steps.build.outputs.digest || inputs.image }}",
        ],
        ["wf-verify-publish-container-dotnet.yml"] =
        [
            "      - name: Build image (Dry Run) @ ${{ inputs.image }}",
            "      - name: Write image metadata @ ${{ steps.build.outputs.digest || inputs.image }}",
        ],
        ["wf-deploy-k8s-aspire.yml"] =
        [
            "      - name: Aspire deploy ${{ inputs.apphost-project }} @ ${{ inputs.aspire-environment }}",
            "      - name: Upload deployment artifacts @ ${{ github.event.repository.name }}-k8s-${{ inputs.environment-name }}-${{ github.run_id }}-${{ github.run_attempt }}",
        ],
        ["wf-verify-deploy-k8s-aspire.yml"] =
        [
            "      - name: Validate deployment inputs (Dry Run) @ ${{ inputs.kubernetes-namespace }}",
            "      - name: Upload deployment artifacts (Dry Run) @ ${{ github.event.repository.name }}-k8s-verify-${{ github.run_id }}-${{ github.run_attempt }}",
        ],
        ["wf-node-lint.yml"] =
        [
            "      - name: Setup Node.js @ ${{ inputs.node-version }} ${{ inputs.package-manager }}",
            "      - name: Lint ${{ inputs.working-directory }} @ ${{ inputs.lint-script }}",
        ],
        ["wf-node-test.yml"] =
        [
            "      - name: Setup Node.js @ ${{ inputs.node-version }} ${{ inputs.package-manager }}",
            "      - name: Test ${{ inputs.working-directory }} @ ${{ inputs.test-script }}",
        ],
        ["wf-node-build.yml"] =
        [
            "      - name: Setup Node.js @ ${{ inputs.node-version }} ${{ inputs.package-manager }}",
            "      - name: Build ${{ inputs.working-directory }} @ ${{ inputs.build-script }}",
        ],
        ["wf-dotnet-format.yml"] =
        [
            "      - name: Verify formatting @ ${{ inputs.solution }}",
            "      - name: Verify JetBrains CleanupCode @ ${{ inputs.profile }}",
        ],
        ["wf-dotnet-test.yml"] =
        [
            "      - name: Test ${{ inputs.solution }} @ ${{ inputs.configuration }} coverage=${{ inputs.coverage }}",
            "      - name: Generate coverage report @ ${{ inputs.coverage-reporttypes }}",
        ],
        ["wf-release-backpropagation.yml"] =
        [
            "      - name: Backpropagate ${{ inputs.release-ref-name }} -> ${{ inputs.default-branch }} @ ${{ inputs.new-version }}",
            "      - name: Publish summary @ ${{ steps.backpropagation.outputs.pr-url || inputs.default-branch }}",
        ],
        ["wf-platform-selftest.yml"] =
        [
            "      - name: Actionlint @ 1.7.12",
            "      - name: Validate workflow contracts @ scripts/validate-workflows.cs",
        ],
        ["wf-lint-github-actions.yml"] =
        [
            "      - name: Set up Python 3.14 + pipx",
            "      - name: Actionlint @ caller workflows",
        ],
    };

    foreach (var (fileName, expectedStepNames) in expectedWorkflowStepNames)
    {
        var path = Path.Combine(repoRoot, ".github", "workflows", fileName);
        if (!File.Exists(path))
        {
            AddFailure($"{path}: workflow file is required for step display name contract.");
            continue;
        }

        var text = File.ReadAllText(path);
        foreach (var expectedStepName in expectedStepNames)
        {
            if (!text.Contains(expectedStepName, StringComparison.Ordinal))
            {
                AddFailure($"{path}: step display name must be '{expectedStepName.Trim()}'.");
            }
        }
    }
}

void ValidateReleaseBackpropagationContract()
{
    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-release-backpropagation.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-release-backpropagation.schema.json");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "release-backpropagation", "action.yml");
    var scriptPath = Path.Combine(repoRoot, ".github", "actions", "release-backpropagation", "run-backpropagation.cs");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: release backpropagation workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);
        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/release-backpropagation", StringComparison.Ordinal)
            || !workflowText.Contains("PR_AUTOMATION_PAT", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: release backpropagation workflow must use release-backpropagation action and pass PR_AUTOMATION_PAT.");
        }

        if (!Regex.IsMatch(workflowText, @"(?m)^\s*environment:\s*\$\{\{\s*inputs\.environment-name\s*\}\}\s*$"))
        {
            AddFailure($"{workflowPath}: release backpropagation workflow must bind the backpropagation job to inputs.environment-name.");
        }
    }

    if (!File.Exists(schemaPath))
    {
        AddFailure($"{schemaPath}: release backpropagation workflow schema is required.");
    }

    if (!File.Exists(actionPath))
    {
        AddFailure($"{actionPath}: release-backpropagation composite action is required.");
    }
    else if (!File.ReadAllText(actionPath).Contains("dotnet run --file", StringComparison.Ordinal))
    {
        AddFailure($"{actionPath}: release-backpropagation action must delegate command logic to a .NET file script.");
    }

    if (!File.Exists(scriptPath))
    {
        AddFailure($"{scriptPath}: release-backpropagation .NET file script is required.");
    }
    else
    {
        var scriptText = File.ReadAllText(scriptPath);
        foreach (var requiredToken in new[] { "#:package CliWrap@", "\"pr\"", "\"new\"", "\"merge\"", "PR_AUTOMATION_PAT" })
        {
            if (!scriptText.Contains(requiredToken, StringComparison.Ordinal))
            {
                AddFailure($"{scriptPath}: release-backpropagation script must contain {requiredToken}.");
            }
        }
    }
}

void ValidateAspireAppHostInputContract()
{
    var aspireWorkflows = new[]
    {
        "wf-deploy-k8s-aspire.yml",
        "wf-verify-deploy-k8s-aspire.yml",
    };

    foreach (var workflowName in aspireWorkflows)
    {
        var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", Path.ChangeExtension(workflowName, ".schema.json"));
        if (!File.Exists(schemaPath))
        {
            continue;
        }

        var schema = ReadWorkflowInputSchema(schemaPath);
        if (!schema.Inputs.TryGetValue("apphost-project", out var appHostProject) || !appHostProject.Required)
        {
            AddFailure($"{schemaPath}: apphost-project must be listed as a required workflow input.");
        }

        if (appHostProject?.HasDefault == true)
        {
            AddFailure($"{schemaPath}: apphost-project must not define a repository-specific default.");
        }
    }
}

void ValidateRepositoryPipelineContract()
{
    const string majorTagPlugin = "semantic-release-major-tag";
    const string pinnedMajorTagPlugin = "semantic-release-major-tag@0.3.2";
    var buildWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "build.yml");
    var releaseWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml");
    var releaseConfigPath = Path.Combine(repoRoot, "release.config.cjs");
    var releasePrerequisiteNeedsBlock =
        "needs:\n"
        + "      - selftest\n"
        + "      - typescript-pnpm-lint\n"
        + "      - typescript-pnpm-test\n"
        + "      - typescript-pnpm-build\n"
        + "      - dotnet-library-format\n"
        + "      - dotnet-library-test\n"
        + "      - dotnet-nuget\n"
        + "      - dotnet-container\n";
    var repositoryPrerequisiteWorkflowUses = new[]
    {
        ("platform selftest", "uses: ./.github/workflows/wf-platform-selftest.yml"),
        ("TypeScript pnpm lint fixture", "uses: ./.github/workflows/wf-node-lint.yml"),
        ("TypeScript pnpm test fixture", "uses: ./.github/workflows/wf-node-test.yml"),
        ("TypeScript pnpm build fixture", "uses: ./.github/workflows/wf-node-build.yml"),
        (".NET library format fixture", "uses: ./.github/workflows/wf-dotnet-format.yml"),
        (".NET library test fixture", "uses: ./.github/workflows/wf-dotnet-test.yml"),
        ("NuGet publish verification fixture", "uses: ./.github/workflows/wf-verify-publish-nuget.yml"),
        ("container image publish verification fixture", "uses: ./.github/workflows/wf-verify-publish-container-dotnet.yml"),
    };
    var localDotNetFormatToolSetup =
        "      working-directory: tests/fixtures/mock-projects/dotnet-nuget-library\n"
        + "      solution: Mock.NuGet.Library.slnx\n"
        + "      restore-locked-mode: true\n"
        + "      restore-tools: false\n"
        + "      install-tool: true\n"
        + "      tool-version: 2026.1.4\n";

    if (File.Exists(buildWorkflowPath))
    {
        AddFailure($"{buildWorkflowPath}: release.yml owns platform selftests; remove build.yml.");
    }

    if (!File.Exists(releaseWorkflowPath))
    {
        AddFailure($"{releaseWorkflowPath}: repository release workflow is required.");
    }
    else
    {
        var releaseText = NormalizeLineEndings(File.ReadAllText(releaseWorkflowPath));
        if (!Regex.IsMatch(releaseText, @"(?m)^\s*push:\s*$")
            || !releaseText.Contains("branches: [main]", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must run on push to main.");
        }

        if (!Regex.IsMatch(releaseText, @"(?m)^\s*pull_request:\s*$")
            || !Regex.IsMatch(releaseText, @"(?m)^\s*workflow_dispatch:\s*$"))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must run on pull_request and manual dispatch.");
        }

        if (WorkflowDefinesInput(releaseWorkflowPath, "dry-run"))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must keep PR behavior event-gated instead of adding a dry-run input.");
        }

        foreach (var (name, requiredUse) in repositoryPrerequisiteWorkflowUses)
        {
            if (!releaseText.Contains(requiredUse, StringComparison.Ordinal))
            {
                AddFailure($"{releaseWorkflowPath}: release workflow must call the {name} reusable workflow before semantic-release.");
            }
        }

        if (!releaseText.Contains("uses: ./.github/workflows/wf-verify-release-semantic.yml", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must call wf-verify-release-semantic.yml for pull requests.");
        }

        if (!releaseText.Contains("uses: ./.github/workflows/wf-verify-release-semantic.yml\n    permissions:\n      contents: write", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: semantic-release verification caller must grant contents: write for dry-run tag push verification.");
        }

        if (!releaseText.Contains("uses: ./.github/workflows/wf-release-semantic.yml", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must call wf-release-semantic.yml.");
        }

        if (Regex.Matches(releaseText, Regex.Escape(releasePrerequisiteNeedsBlock)).Count < 2)
        {
            AddFailure($"{releaseWorkflowPath}: semantic-release verification and publication jobs must depend on all fixture dogfood jobs.");
        }

        if (!releaseText.Contains(localDotNetFormatToolSetup, StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: local .NET format fixture must install pinned JetBrains tools and diff only the fixture workspace.");
        }

        if (!releaseText.Contains("if: github.event_name == 'pull_request'", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: semantic-release verification job must be limited to pull_request events.");
        }

        if (!releaseText.Contains("if: github.event_name != 'pull_request'", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: semantic-release publication job must not run on pull_request events.");
        }

        if (!releaseText.Contains(pinnedMajorTagPlugin, StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must install {pinnedMajorTagPlugin} for vN tag updates.");
        }
    }

    if (!File.Exists(releaseConfigPath))
    {
        AddFailure($"{releaseConfigPath}: semantic-release config is required.");
    }
    else
    {
        var releaseConfigText = File.ReadAllText(releaseConfigPath);
        if (releaseConfigText.Contains("@semantic-release/exec", StringComparison.Ordinal))
        {
            AddFailure($"{releaseConfigPath}: @semantic-release/exec is not allowed.");
        }

        if (releaseConfigText.Contains("@semantic-release/npm", StringComparison.Ordinal))
        {
            AddFailure($"{releaseConfigPath}: this platform repository must not publish npm packages.");
        }

        if (!releaseConfigText.Contains("@semantic-release/github", StringComparison.Ordinal))
        {
            AddFailure($"{releaseConfigPath}: semantic-release config must publish GitHub release metadata.");
        }

        if (!releaseConfigText.Contains(majorTagPlugin, StringComparison.Ordinal))
        {
            AddFailure($"{releaseConfigPath}: semantic-release config must create/update vN major version tags with {majorTagPlugin}.");
        }
    }
}

void ValidateLocalActContract()
{
    var actConfigPath = Path.Combine(repoRoot, ".actrc");
    var gitIgnorePath = Path.Combine(repoRoot, ".gitignore");
    var readmePath = Path.Combine(repoRoot, "README.md");
    var actScriptPath = Path.Combine(repoRoot, "scripts", "act-local.ps1");

    var requiredActConfigLines = new[]
    {
        "--container-architecture=linux/amd64",
        "-P ubuntu-latest=ghcr.io/catthehacker/ubuntu:act-latest",
        "--artifact-server-path=.act-artifacts",
        "--pull=false",
    };

    if (!File.Exists(actConfigPath))
    {
        AddFailure($"{actConfigPath}: repo-local act configuration is required.");
    }
    else
    {
        var actConfigText = File.ReadAllText(actConfigPath);
        foreach (var requiredLine in requiredActConfigLines)
        {
            if (!actConfigText.Contains(requiredLine, StringComparison.Ordinal))
            {
                AddFailure($"{actConfigPath}: act config must contain {requiredLine}.");
            }
        }
    }

    if (!File.Exists(gitIgnorePath)
        || !File.ReadAllText(gitIgnorePath).Contains(".act-artifacts/", StringComparison.Ordinal))
    {
        AddFailure($"{gitIgnorePath}: local act artifact output must be ignored.");
    }

    if (!File.Exists(actScriptPath)
        || !File.ReadAllText(actScriptPath).Contains("dockerDesktopLinuxEngine", StringComparison.Ordinal))
    {
        AddFailure($"{actScriptPath}: Windows act launcher must set the Docker Desktop Linux engine pipe.");
    }

    if (!File.Exists(readmePath)
        || !File.ReadAllText(readmePath).Contains("scripts/act-local.ps1", StringComparison.Ordinal))
    {
        AddFailure($"{readmePath}: local validation docs must use the act launcher.");
    }
}

void ValidateCacheOptOutContract(string file, string text, string[] lines, bool isWorkflow)
{
    if (!text.Contains("uses: runs-on/cache@", StringComparison.Ordinal))
    {
        return;
    }

    var enableCacheInput = isWorkflow
        ? ReadWorkflowInputs(file).GetValueOrDefault("enable-cache")
        : ReadActionInputs(file).GetValueOrDefault("enable-cache");

    if (enableCacheInput is null)
    {
        AddFailure($"{file}: runs-on/cache consumers must expose an enable-cache input.");
        return;
    }

    if (isWorkflow)
    {
        if (!string.Equals(NormalizeWorkflowInputType(enableCacheInput.Type), "boolean", StringComparison.Ordinal))
        {
            AddFailure($"{file}: workflow enable-cache input must be boolean.");
        }

        if (!string.Equals(NormalizeWorkflowDefault(enableCacheInput), "boolean:true", StringComparison.Ordinal))
        {
            AddFailure($"{file}: enable-cache should default to true for existing caller compatibility.");
        }
    }
    else if (!string.Equals(NormalizeWorkflowDefault(enableCacheInput), "string:true", StringComparison.Ordinal))
    {
        AddFailure($"{file}: composite enable-cache input should default to \"true\".");
    }

    for (var index = 0; index < lines.Length; index++)
    {
        if (!lines[index].Contains("uses: runs-on/cache@", StringComparison.Ordinal))
        {
            continue;
        }

        var stepStart = index;
        while (stepStart > 0 && !Regex.IsMatch(lines[stepStart], @"^\s*-\s+name:\s*"))
        {
            stepStart--;
        }

        var stepEnd = lines.Length - 1;
        for (var lookAhead = index + 1; lookAhead < lines.Length; lookAhead++)
        {
            if (Regex.IsMatch(lines[lookAhead], @"^\s*-\s+name:\s*"))
            {
                stepEnd = lookAhead - 1;
                break;
            }
        }

        var stepText = string.Join('\n', lines[stepStart..(stepEnd + 1)]);
        var expectedIf = isWorkflow ? "if: inputs.enable-cache" : "if: inputs.enable-cache == 'true'";
        if (!stepText.Contains(expectedIf, StringComparison.Ordinal))
        {
            AddFailure($"{file}:{index + 1}: runs-on/cache step must be gated with '{expectedIf}'.");
        }
    }
}

Dictionary<string, WorkflowInput> ReadWorkflowInputs(string workflowPath) =>
    ReadWorkflowFile(workflowPath).On?.WorkflowCall?.Inputs ?? new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);

Dictionary<string, WorkflowInput> ReadActionInputs(string actionPath) =>
    ReadActionFile(actionPath).Inputs ?? new Dictionary<string, WorkflowInput>(StringComparer.Ordinal);

WorkflowFile ReadWorkflowFile(string workflowPath) =>
    ReadYaml<WorkflowFile>(workflowPath);

ActionFile ReadActionFile(string actionPath) =>
    ReadYaml<ActionFile>(actionPath);

T ReadYaml<T>(string path)
{
    var deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    return deserializer.Deserialize<T>(File.ReadAllText(path));
}

WorkflowInputSchema ReadWorkflowInputSchema(string schemaPath)
{
    using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath), new JsonDocumentOptions
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    });

    var root = schema.RootElement;
    var required = new HashSet<string>(StringComparer.Ordinal);
    if (root.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in requiredElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } inputName)
            {
                required.Add(inputName);
            }
        }
    }

    var inputs = new Dictionary<string, SchemaInput>(StringComparer.Ordinal);
    if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
    {
        return new WorkflowInputSchema(inputs);
    }

    foreach (var property in properties.EnumerateObject())
    {
        var definition = property.Value;
        var hasDefault = definition.TryGetProperty("default", out var defaultElement);
        inputs[property.Name] = new SchemaInput(
            Type: ReadSchemaType(definition),
            Required: required.Contains(property.Name),
            HasDefault: hasDefault,
            DefaultValue: hasDefault ? NormalizeSchemaDefault(defaultElement) : string.Empty,
            DisplayDefault: hasDefault ? FormatSchemaDisplayDefault(defaultElement) : "none");
    }

    return new WorkflowInputSchema(inputs);
}

static string ReadSchemaType(JsonElement definition)
{
    if (!definition.TryGetProperty("type", out var typeElement))
    {
        return "unspecified";
    }

    if (typeElement.ValueKind == JsonValueKind.String)
    {
        return NormalizeSchemaType(typeElement.GetString());
    }

    if (typeElement.ValueKind == JsonValueKind.Array)
    {
        return string.Join("/", typeElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeSchemaType(item.GetString())));
    }

    return "unspecified";
}

bool WorkflowDefinesInput(string workflowPath, string inputName) =>
    ReadWorkflowInputs(workflowPath).ContainsKey(inputName);

bool ActionDefinesInput(string actionPath, string inputName) =>
    ReadActionInputs(actionPath).ContainsKey(inputName);

static bool InputTypesMatch(string workflowType, string schemaType) =>
    string.Equals(workflowType, schemaType, StringComparison.Ordinal)
    || (string.Equals(workflowType, "number", StringComparison.Ordinal)
        && string.Equals(schemaType, "integer", StringComparison.Ordinal));

static string NormalizeWorkflowInputType(string? type) =>
    string.IsNullOrWhiteSpace(type) ? "unspecified" : type.Trim();

static string NormalizeSchemaType(string? type) =>
    string.IsNullOrWhiteSpace(type) ? "unspecified" : type.Trim();

static string NormalizeWorkflowDefault(WorkflowInput input)
{
    var value = input.Default;
    if (value is null)
    {
        return "missing:";
    }

    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    return NormalizeWorkflowInputType(input.Type) switch
    {
        "boolean" when bool.TryParse(text, out var boolValue) => $"boolean:{boolValue.ToString().ToLowerInvariant()}",
        "number" => $"number:{text}",
        "string" => $"string:{text}",
        _ => value switch
        {
            bool boolValue => $"boolean:{boolValue.ToString().ToLowerInvariant()}",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => $"number:{text}",
            string => $"string:{text}",
            _ => $"object:{text}"
        }
    };
}

static string NormalizeSchemaDefault(JsonElement value) =>
    value.ValueKind switch
    {
        JsonValueKind.String => $"string:{value.GetString()}",
        JsonValueKind.True => "boolean:true",
        JsonValueKind.False => "boolean:false",
        JsonValueKind.Number => $"number:{value.GetRawText()}",
        JsonValueKind.Null => "null:",
        _ => $"object:{value.GetRawText()}"
    };

static string FormatDisplayDefault(object? value) =>
    value switch
    {
        null => "none",
        string stringValue => stringValue,
        bool boolValue => boolValue.ToString().ToLowerInvariant(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

static string FormatSchemaDisplayDefault(JsonElement value) =>
    value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

static string NormalizeLineEndings(string text) =>
    text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

void ValidateUsesReferences(string file, string[] lines)
{
    for (var index = 0; index < lines.Length; index++)
    {
        var line = lines[index].Split('#', 2)[0];
        var match = Regex.Match(line, @"^\s*uses:\s*(?<target>\S+)");
        if (!match.Success)
        {
            continue;
        }

        var target = match.Groups["target"].Value.Trim('"', '\'');
        if (target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith(@".\", StringComparison.Ordinal))
        {
            continue;
        }

        var refIndex = target.LastIndexOf('@');
        if (refIndex < 0 || refIndex == target.Length - 1)
        {
            AddFailure($"{file}:{index + 1}: external uses reference must include a ref: {target}");
            continue;
        }

        var reference = target[(refIndex + 1)..];
        if (!Regex.IsMatch(reference, @"^v\d+(\.\d+){0,2}([.-][A-Za-z0-9]+)?$"))
        {
            AddFailure($"{file}:{index + 1}: external uses reference should use a version tag, not '{reference}': {target}");
        }
    }
}

async Task RunActionlintWhenAvailableAsync()
{
    var actionlint = FindExecutableOnPath("actionlint");
    if (actionlint is null)
    {
        Console.WriteLine("actionlint not found; skipped actionlint syntax pass.");
        return;
    }

    var result = await Cli.Wrap(actionlint)
        .WithArguments(["-color"])
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    Console.Write(result.StandardOutput);
    Console.Error.Write(result.StandardError);

    if (result.ExitCode != 0)
    {
        AddFailure($"actionlint failed with exit code {result.ExitCode}.");
    }
}

async Task ValidateGeneratedWorkflowDocsAsync()
{
    var generatorPath = Path.Combine(repoRoot, "scripts", "generate-docs.cs");
    if (!File.Exists(generatorPath))
    {
        AddFailure($"{generatorPath}: generated workflow docs script is required.");
        return;
    }

    var dotnet = FindExecutableOnPath("dotnet");
    if (dotnet is null)
    {
        AddFailure("dotnet executable is required to check generated workflow docs.");
        return;
    }

    var result = await Cli.Wrap(dotnet)
        .WithArguments(["run", "--file", generatorPath, "--", "--check"])
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    Console.Write(result.StandardOutput);
    Console.Error.Write(result.StandardError);

    if (result.ExitCode != 0)
    {
        AddFailure($"generated workflow docs are stale; run `dotnet run --file scripts/generate-docs.cs`.");
    }
}

void AddFailure(string message)
{
    failures.Add(message);
    Console.Error.WriteLine($"ERROR: {message}");
}

static bool IsYamlFile(string path)
{
    var extension = Path.GetExtension(path);
    return string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase);
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (IsRepositoryRoot(current))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (IsRepositoryRoot(current))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate repository root.");
}

static bool IsRepositoryRoot(DirectoryInfo directory)
{
    var gitPath = Path.Combine(directory.FullName, ".git");
    return Directory.Exists(gitPath) || File.Exists(gitPath);
}

static string? FindExecutableOnPath(string executableName)
{
    var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var extensions = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
        : [string.Empty];

    foreach (var path in paths)
    {
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(path, executableName + extension.ToLowerInvariant());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(path, executableName + extension.ToUpperInvariant());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    return null;
}

sealed class WorkflowFile
{
    [YamlMember(Alias = "on", ApplyNamingConventions = false)]
    public WorkflowTriggers? On { get; set; }
}

sealed class WorkflowTriggers
{
    [YamlMember(Alias = "workflow_call", ApplyNamingConventions = false)]
    public WorkflowCall? WorkflowCall { get; set; }
}

sealed class WorkflowCall
{
    [YamlMember(Alias = "inputs", ApplyNamingConventions = false)]
    public Dictionary<string, WorkflowInput> Inputs { get; set; } = new(StringComparer.Ordinal);
}

sealed class ActionFile
{
    [YamlMember(Alias = "inputs", ApplyNamingConventions = false)]
    public Dictionary<string, WorkflowInput> Inputs { get; set; } = new(StringComparer.Ordinal);
}

sealed class WorkflowInput
{
    [YamlMember(Alias = "type", ApplyNamingConventions = false)]
    public string? Type { get; set; }

    [YamlMember(Alias = "required", ApplyNamingConventions = false)]
    public bool Required { get; set; }

    [YamlMember(Alias = "default", ApplyNamingConventions = false)]
    public object? Default { get; set; }

    public bool HasDefault => Default is not null;
}

sealed record WorkflowInputSchema(Dictionary<string, SchemaInput> Inputs);

sealed record SchemaInput(
    string Type,
    bool Required,
    bool HasDefault,
    string DefaultValue,
    string DisplayDefault);
