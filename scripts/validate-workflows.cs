#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:package CliWrap@3.9.0

using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

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

var failures = new List<string>();

ValidateWorkflowInputSchemas();
ValidateWorkflows();
ValidateCompositeActions();
ValidateContainerPublishContract();
ValidateRepositoryPipelineContract();
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
    var oldWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-build-container.yml");
    if (File.Exists(oldWorkflowPath))
    {
        AddFailure($"{oldWorkflowPath}: container workflow has been renamed to wf-publish-container.yml.");
    }

    var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "wf-publish-container.yml");
    var schemaPath = Path.Combine(repoRoot, "schemas", "workflow-inputs", "wf-publish-container.schema.json");
    var actionPath = Path.Combine(repoRoot, ".github", "actions", "dotnet-setversion", "action.yml");

    if (!File.Exists(workflowPath))
    {
        AddFailure($"{workflowPath}: publish container workflow is required.");
    }
    else
    {
        var workflowText = File.ReadAllText(workflowPath);
        var workflowLines = File.ReadAllLines(workflowPath);
        if (GetYamlBlock(workflowLines, "dotnet-setversion") is null)
        {
            AddFailure($"{workflowPath}: publish container workflow must expose dotnet-setversion input.");
        }

        if (GetYamlBlock(workflowLines, "version") is null)
        {
            AddFailure($"{workflowPath}: publish container workflow must expose bare version input.");
        }

        if (GetYamlBlock(workflowLines, "build-args") is null)
        {
            AddFailure($"{workflowPath}: publish container workflow must expose Docker build-args input.");
        }

        if (!workflowText.Contains("uses: ./.ci/arkanis-ci/.github/actions/dotnet-setversion", StringComparison.Ordinal))
        {
            AddFailure($"{workflowPath}: publish container workflow must use the dotnet-setversion action when enabled.");
        }
    }

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
        var actionLines = File.ReadAllLines(actionPath);
        foreach (var requiredInput in new[] { "version", "working-directory", "recursive", "tool-version" })
        {
            if (GetYamlBlock(actionLines, requiredInput) is null)
            {
                AddFailure($"{actionPath}: dotnet-setversion action must expose {requiredInput} input.");
            }
        }
    }
}

void ValidateRepositoryPipelineContract()
{
    var buildWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "build.yml");
    var releaseWorkflowPath = Path.Combine(repoRoot, ".github", "workflows", "release.yml");
    var releaseConfigPath = Path.Combine(repoRoot, "release.config.cjs");

    if (!File.Exists(buildWorkflowPath))
    {
        AddFailure($"{buildWorkflowPath}: repository build workflow is required.");
    }
    else
    {
        var buildText = File.ReadAllText(buildWorkflowPath);
        if (!Regex.IsMatch(buildText, @"(?m)^\s*pull_request:\s*$")
            || !Regex.IsMatch(buildText, @"(?m)^\s*push:\s*$")
            || !buildText.Contains("branches: [main]", StringComparison.Ordinal))
        {
            AddFailure($"{buildWorkflowPath}: build workflow must run on pull_request and push to main.");
        }

        if (!buildText.Contains("uses: ./.github/workflows/wf-platform-selftest.yml", StringComparison.Ordinal))
        {
            AddFailure($"{buildWorkflowPath}: build workflow must call wf-platform-selftest.yml.");
        }
    }

    if (!File.Exists(releaseWorkflowPath))
    {
        AddFailure($"{releaseWorkflowPath}: repository release workflow is required.");
    }
    else
    {
        var releaseText = File.ReadAllText(releaseWorkflowPath);
        if (!Regex.IsMatch(releaseText, @"(?m)^\s*push:\s*$")
            || !releaseText.Contains("branches: [main]", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must run on push to main.");
        }

        if (!releaseText.Contains("uses: ./.github/workflows/wf-platform-selftest.yml", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must run platform selftest before release.");
        }

        if (!releaseText.Contains("uses: ./.github/workflows/wf-release-semantic.yml", StringComparison.Ordinal))
        {
            AddFailure($"{releaseWorkflowPath}: release workflow must call wf-release-semantic.yml.");
        }

        if (!Regex.IsMatch(releaseText, @"(?m)^\s*needs:\s*selftest\s*$"))
        {
            AddFailure($"{releaseWorkflowPath}: semantic release job must depend on selftest.");
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
    }
}

void ValidateCacheOptOutContract(string file, string text, string[] lines, bool isWorkflow)
{
    if (!text.Contains("uses: runs-on/cache@", StringComparison.Ordinal))
    {
        return;
    }

    var enableCacheBlock = GetYamlBlock(lines, "enable-cache");
    if (enableCacheBlock is null)
    {
        AddFailure($"{file}: runs-on/cache consumers must expose an enable-cache input.");
        return;
    }

    if (isWorkflow)
    {
        if (!Regex.IsMatch(enableCacheBlock, @"(?m)^\s*type:\s*boolean\s*$"))
        {
            AddFailure($"{file}: workflow enable-cache input must be boolean.");
        }

        if (!Regex.IsMatch(enableCacheBlock, @"(?m)^\s*default:\s*true\s*$"))
        {
            AddFailure($"{file}: enable-cache should default to true for existing caller compatibility.");
        }
    }
    else if (!Regex.IsMatch(enableCacheBlock, @"(?m)^\s*default:\s*[""']true[""']\s*$"))
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

static string? GetYamlBlock(string[] lines, string key)
{
    for (var index = 0; index < lines.Length; index++)
    {
        var match = Regex.Match(lines[index], $@"^(?<indent>\s*){Regex.Escape(key)}:\s*(?:#.*)?$");
        if (!match.Success)
        {
            continue;
        }

        var indent = match.Groups["indent"].Value.Length;
        var block = new List<string> { lines[index] };
        for (var lookAhead = index + 1; lookAhead < lines.Length; lookAhead++)
        {
            var line = lines[lookAhead];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                block.Add(line);
                continue;
            }

            var currentIndent = line.TakeWhile(char.IsWhiteSpace).Count();
            if (currentIndent <= indent)
            {
                break;
            }

            block.Add(line);
        }

        return string.Join('\n', block);
    }

    return null;
}

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
        if (Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate repository root.");
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
