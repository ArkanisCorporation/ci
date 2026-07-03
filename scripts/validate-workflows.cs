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

ValidateWorkflows();
ValidateCompositeActions();
await RunActionlintWhenAvailableAsync();

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Workflow validation failed with {failures.Count} issue(s).");
    return 1;
}

Console.WriteLine("Workflow validation passed.");
return 0;

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
