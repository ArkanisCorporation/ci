#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:package CliWrap@3.9.0

using CliWrap;
using CliWrap.Buffered;

/*
 * Summary:
 *   Installs ReportGenerator, generates coverage reports, and optionally comments on pull requests.
 *
 * Remarks:
 *   The script is intended to run from the dotnet-coverage-report composite action.
 *   It uses CliWrap for native command execution.
 *   It writes outputs for the report directory and Markdown summary path.
 */

return await RunAsync();

static async Task<int> RunAsync()
{
    var workspace = RequiredEnvironment("GITHUB_WORKSPACE");
    var reports = EnvironmentOrDefault("ACTION_INPUT_REPORTS", "artifacts/coverage/raw/**/coverage.cobertura.xml");
    var targetDirectory = ResolvePath(EnvironmentOrDefault("ACTION_INPUT_TARGETDIR", "artifacts/coverage/report"), workspace);
    var reportTypes = EnvironmentOrDefault("ACTION_INPUT_REPORTTYPES", "HtmlInline;Cobertura;MarkdownSummaryGithub;TextSummary");
    var assemblyFilters = EnvironmentOrDefault("ACTION_INPUT_ASSEMBLYFILTERS", "+*;-*.UnitTests;-*.IntegrationTests");
    var customSettings = EnvironmentOrDefault("ACTION_INPUT_CUSTOM_SETTINGS", string.Empty);
    var tag = EnvironmentOrDefault("ACTION_INPUT_TAG", string.Empty);
    var toolVersion = EnvironmentOrDefault("ACTION_INPUT_TOOL_VERSION", "5.5.10");
    var commentOnPr = ParseBoolean("ACTION_INPUT_COMMENT_ON_PR", defaultValue: true);
    var failIfNoReports = ParseBoolean("ACTION_INPUT_FAIL_IF_NO_REPORTS", defaultValue: true);
    var prNumber = EnvironmentOrDefault("ACTION_INPUT_PR_NUMBER", string.Empty);

    Directory.CreateDirectory(targetDirectory);

    var matchedReports = ExpandReportPatterns(reports, workspace).ToArray();
    if (matchedReports.Length == 0)
    {
        var message = $"No coverage reports matched: {reports}";
        if (failIfNoReports)
        {
            Console.Error.WriteLine(message);
            return 1;
        }

        Console.WriteLine(message);
        return 0;
    }

    var executable = await InstallReportGeneratorAsync(toolVersion);
    if (executable is null)
    {
        return 1;
    }

    var arguments = new List<string>
    {
        $"-reports:{reports}",
        $"-targetdir:{targetDirectory}",
        $"-reporttypes:{reportTypes}",
        $"-assemblyfilters:{assemblyFilters}"
    };

    if (!string.IsNullOrWhiteSpace(tag))
    {
        arguments.Add($"-tag:{tag}");
    }

    if (!string.IsNullOrWhiteSpace(customSettings))
    {
        arguments.Add($"-customSettings:{customSettings}");
    }

    var reportResult = await RunCommandAsync(executable, arguments, workspace, allowFailure: true);
    if (reportResult.ExitCode != 0)
    {
        return reportResult.ExitCode;
    }

    var summaryFile = Path.Combine(targetDirectory, "SummaryGithub.md");
    WriteOutput("report-directory", targetDirectory);
    WriteOutput("summary-file", summaryFile);

    if (File.Exists(summaryFile))
    {
        await AppendStepSummaryAsync(await File.ReadAllTextAsync(summaryFile));
    }
    else
    {
        Console.WriteLine($"Coverage Markdown summary not found: {summaryFile}");
    }

    if (commentOnPr && !string.IsNullOrWhiteSpace(prNumber) && File.Exists(summaryFile))
    {
        var commentResult = await RunCommandAsync(
            "gh",
            ["pr", "comment", prNumber, "--edit-last", "--create-if-none", "--body-file", summaryFile],
            workspace,
            allowFailure: true);

        if (commentResult.ExitCode != 0)
        {
            return commentResult.ExitCode;
        }
    }

    return 0;
}

static async Task<string?> InstallReportGeneratorAsync(string toolVersion)
{
    var runnerTemp = RequiredEnvironment("RUNNER_TEMP");
    var toolPath = Path.Combine(runnerTemp, "arkanis-reportgenerator");

    if (Directory.Exists(toolPath))
    {
        Directory.Delete(toolPath, recursive: true);
    }

    Directory.CreateDirectory(toolPath);

    var result = await RunCommandAsync(
        "dotnet",
        ["tool", "install", "dotnet-reportgenerator-globaltool", "--tool-path", toolPath, "--version", toolVersion, "--allow-roll-forward"],
        Directory.GetCurrentDirectory(),
        allowFailure: true);

    if (result.ExitCode != 0)
    {
        return null;
    }

    foreach (var candidate in new[] { Path.Combine(toolPath, "reportgenerator"), Path.Combine(toolPath, "reportgenerator.exe") })
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    Console.Error.WriteLine($"ReportGenerator installed but executable was not found in {toolPath}.");
    return null;
}

static IEnumerable<string> ExpandReportPatterns(string patterns, string workspace)
{
    foreach (var pattern in patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            var literal = ResolvePath(pattern, workspace);
            if (File.Exists(literal))
            {
                yield return literal;
            }

            continue;
        }

        var fileName = Path.GetFileName(pattern);
        var beforeWildcard = pattern.Split('*', 2)[0].TrimEnd('/', '\\');
        var searchRoot = string.IsNullOrWhiteSpace(beforeWildcard)
            ? workspace
            : ResolvePath(beforeWildcard, workspace);

        while (!Directory.Exists(searchRoot) && searchRoot.Length > Path.GetPathRoot(searchRoot)?.Length)
        {
            searchRoot = Directory.GetParent(searchRoot)?.FullName ?? workspace;
        }

        if (!Directory.Exists(searchRoot))
        {
            continue;
        }

        foreach (var file in Directory.EnumerateFiles(searchRoot, fileName, SearchOption.AllDirectories))
        {
            yield return file;
        }
    }
}

static async Task<CommandRunResult> RunCommandAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool allowFailure)
{
    Console.WriteLine(RenderCommand(executable, arguments));

    var result = await Cli.Wrap(executable)
        .WithArguments(arguments)
        .WithWorkingDirectory(workingDirectory)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    Console.Write(result.StandardOutput);
    Console.Error.Write(result.StandardError);

    if (!allowFailure && result.ExitCode != 0)
    {
        Console.Error.WriteLine($"{executable} failed with exit code {result.ExitCode}.");
    }

    return new CommandRunResult(result.ExitCode, result.StandardOutput, result.StandardError);
}

static string RequiredEnvironment(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Required environment variable is missing: {name}");
    }

    return value;
}

static string EnvironmentOrDefault(string name, string defaultValue)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrEmpty(value) ? defaultValue : value;
}

static bool ParseBoolean(string name, bool defaultValue)
{
    var rawValue = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        return defaultValue;
    }

    return rawValue.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => throw new InvalidOperationException($"{name} must be true or false.")
    };
}

static string ResolvePath(string path, string basePath)
{
    return Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(basePath, path));
}

static string RenderCommand(string executable, IEnumerable<string> arguments)
{
    return string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForLog));
}

static string QuoteForLog(string value)
{
    return value.Any(char.IsWhiteSpace)
        ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
        : value;
}

static void WriteOutput(string name, string value)
{
    var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (string.IsNullOrWhiteSpace(outputPath))
    {
        return;
    }

    File.AppendAllText(outputPath, $"{name}={value}{Environment.NewLine}");
}

static async Task AppendStepSummaryAsync(string markdown)
{
    var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (string.IsNullOrWhiteSpace(summaryPath))
    {
        return;
    }

    await File.AppendAllTextAsync(summaryPath, markdown);
}

sealed record CommandRunResult(int ExitCode, string StandardOutput, string StandardError);
