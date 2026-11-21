#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:package CliWrap@3.9.0

using System.Globalization;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

/*
 * Summary:
 *   Runs JetBrains ReSharper CleanupCode and reports Git diff diagnostics.
 *
 * Remarks:
 *   The script is intended to run from the dotnet-jetbrains-cleanupcode composite action.
 *   It mutates workspace files while CleanupCode runs.
 *   It writes diagnostics under the configured artifacts path.
 *   It uses CliWrap for native command execution.
 */

return await RunAsync();

static async Task<int> RunAsync()
{
    var solution = RequiredEnvironment("ACTION_INPUT_SOLUTION");
    var githubWorkspace = RequiredEnvironment("GITHUB_WORKSPACE");
    var workingDirectory = ResolvePath(EnvironmentOrDefault("ACTION_INPUT_WORKING_DIRECTORY", "."), githubWorkspace);
    var artifactsPath = ResolvePath(EnvironmentOrDefault("ACTION_INPUT_ARTIFACTS_PATH", "artifacts/jetbrains-cleanupcode"), githubWorkspace);
    var profile = EnvironmentOrDefault("ACTION_INPUT_PROFILE", "Built-in: Reformat & Apply Syntax Style");
    var include = EnvironmentOrDefault("ACTION_INPUT_INCLUDE", string.Empty);
    var exclude = EnvironmentOrDefault("ACTION_INPUT_EXCLUDE", "**/*.razor;**/*.svg;**/*.md");
    var remediationMessage = EnvironmentOrDefault("ACTION_INPUT_REMEDIATION_MESSAGE", "Run `dotnet husky run --name dotnet-cleanupcode` locally and commit the resulting changes.");
    var toolVersion = EnvironmentOrDefault("ACTION_INPUT_TOOL_VERSION", ToolDefaults.ReSharperGlobalTools);
    var noUpdates = ParseBoolean("ACTION_INPUT_NO_UPDATES", defaultValue: true);
    var restoreTools = ParseBoolean("ACTION_INPUT_RESTORE_TOOLS", defaultValue: true);
    var installTool = ParseBoolean("ACTION_INPUT_INSTALL_TOOL", defaultValue: false);
    var failOnDiff = ParseBoolean("ACTION_INPUT_FAIL_ON_DIFF", defaultValue: true);
    var diffPreviewLines = ParsePositiveInteger("ACTION_INPUT_DIFF_PREVIEW_LINES", defaultValue: 300);

    if (!Directory.Exists(workingDirectory))
    {
        Console.Error.WriteLine($"working-directory does not exist: {workingDirectory}");
        return 1;
    }

    Directory.CreateDirectory(artifactsPath);
    WriteOutput("artifacts-path", artifactsPath);
    WriteOutput("changed-files", Path.Combine(artifactsPath, "changed-files.txt"));

    if (restoreTools)
    {
        var restoreExitCode = await RestoreLocalToolsAsync(workingDirectory);
        if (restoreExitCode != 0)
        {
            return restoreExitCode;
        }
    }

    var jbExecutable = string.Empty;
    if (installTool)
    {
        var installResult = await InstallJetBrainsToolAsync(toolVersion);
        if (installResult.ExitCode != 0)
        {
            return installResult.ExitCode;
        }

        jbExecutable = installResult.ExecutablePath;
    }

    var cleanupResult = await RunCleanupCodeAsync(
        workingDirectory,
        artifactsPath,
        solution,
        profile,
        include,
        exclude,
        noUpdates,
        installTool,
        jbExecutable);

    // A CleanupCode run that did not finish says nothing about formatting: a crash before the
    // tool touched any file leaves an empty diff, which used to be reported as "no Git diff".
    // Stop here so the failure is the tool's exit code, not a misleading pass message (ARK-745).
    if (cleanupResult.ExitCode != 0)
    {
        await ReportCleanupFailureAsync(cleanupResult.ExitCode, artifactsPath);
        return cleanupResult.ExitCode;
    }

    return await ReportDiffAsync(workingDirectory, artifactsPath, remediationMessage, diffPreviewLines, failOnDiff);
}

static async Task ReportCleanupFailureAsync(int exitCode, string artifactsPath)
{
    var logPath = Path.Combine(artifactsPath, "cleanupcode.log");
    Console.WriteLine($"::error title=JetBrains CleanupCode failed::jb cleanupcode exited with code {exitCode}. The Git diff check was skipped because the tool did not finish. See {logPath}.");
    await AppendStepSummaryAsync($"""
        ## JetBrains CleanupCode failed

        `jb cleanupcode` exited with code {exitCode} before it finished, so the Git diff check was skipped.
        This is a tool failure, not a formatting result. The full output is in `{logPath}`.

        """);
}

static async Task<int> RestoreLocalToolsAsync(string workingDirectory)
{
    var firstAttempt = await RunCommandAsync("dotnet", ["tool", "restore"], workingDirectory, allowFailure: true);
    if (firstAttempt.ExitCode == 0)
    {
        return 0;
    }

    Console.Error.WriteLine("First dotnet tool restore failed; retrying once.");
    var secondAttempt = await RunCommandAsync("dotnet", ["tool", "restore"], workingDirectory, allowFailure: true);
    return secondAttempt.ExitCode;
}

static async Task<ToolInstallResult> InstallJetBrainsToolAsync(string toolVersion)
{
    var runnerTemp = RequiredEnvironment("RUNNER_TEMP");
    var toolPath = Path.Combine(runnerTemp, "arkanis-jetbrains-resharper");

    if (Directory.Exists(toolPath))
    {
        Directory.Delete(toolPath, recursive: true);
    }

    Directory.CreateDirectory(toolPath);

    // No --allow-roll-forward (ARK-745). The flag rewrites the installed tool's runtimeconfig from the
    // package's rollForward=LatestMajor to Major. JetBrains writes the framework version with four
    // parts ("10.0.0.0"); under Major the host (hostfxr 10.0.15 and 11.0.0, seen in COREHOST_TRACE)
    // does not honour that as a lower bound and selects the lowest installed runtime - 8.0.34 on
    // ubuntu-latest - so the net10.0 tool (2026.3+) aborts with "Could not load file or assembly
    // 'System.Runtime, Version=10.0.0.0'" (exit 134). The same runtimeconfig rewritten to "10.0.0"
    // resolves 10.0.x correctly. The package's own LatestMajor picks the newest runtime and works.
    var installArguments = new List<string>
    {
        "tool",
        "install",
        "JetBrains.ReSharper.GlobalTools",
        "--tool-path",
        toolPath,
        "--version",
        toolVersion
    };

    var installResult = await RunCommandAsync("dotnet", installArguments, Directory.GetCurrentDirectory(), allowFailure: true);
    if (installResult.ExitCode != 0)
    {
        return new ToolInstallResult(installResult.ExitCode, string.Empty);
    }

    var executablePath = ResolveToolExecutable(toolPath, "jb");
    if (executablePath is null)
    {
        Console.Error.WriteLine($"JetBrains.ReSharper.GlobalTools installed but jb executable was not found in {toolPath}.");
        return new ToolInstallResult(1, string.Empty);
    }

    return new ToolInstallResult(0, executablePath);
}

static async Task<CommandRunResult> RunCleanupCodeAsync(
    string workingDirectory,
    string artifactsPath,
    string solution,
    string profile,
    string include,
    string exclude,
    bool noUpdates,
    bool installTool,
    string jbExecutable)
{
    var arguments = new List<string>();
    string executable;
    if (installTool)
    {
        executable = jbExecutable;
    }
    else
    {
        executable = "dotnet";
        arguments.Add("jb");
    }

    arguments.Add("cleanupcode");
    arguments.Add(solution);
    arguments.Add($"--profile={profile}");

    if (!string.IsNullOrWhiteSpace(include))
    {
        arguments.Add($"--include={include}");
    }

    if (!string.IsNullOrWhiteSpace(exclude))
    {
        arguments.Add($"--exclude={exclude}");
    }

    if (noUpdates)
    {
        arguments.Add("--no-updates");
    }

    var logBuilder = new StringBuilder();
    logBuilder.AppendLine(CultureInfo.InvariantCulture, $"CleanupCode solution: {solution}");
    logBuilder.AppendLine(CultureInfo.InvariantCulture, $"CleanupCode profile: {profile}");
    logBuilder.AppendLine(CultureInfo.InvariantCulture, $"CleanupCode include: {NullWhenEmpty(include)}");
    logBuilder.AppendLine(CultureInfo.InvariantCulture, $"CleanupCode exclude: {NullWhenEmpty(exclude)}");
    logBuilder.AppendLine(CultureInfo.InvariantCulture, $"CleanupCode command: {RenderCommand(executable, arguments)}");

    Console.Write(logBuilder.ToString());
    var result = await RunCommandAsync(executable, arguments, workingDirectory, allowFailure: true);
    await File.WriteAllTextAsync(
        Path.Combine(artifactsPath, "cleanupcode.log"),
        logBuilder + result.StandardOutput + result.StandardError);

    return result;
}

static async Task<int> ReportDiffAsync(
    string workingDirectory,
    string artifactsPath,
    string remediationMessage,
    int diffPreviewLines,
    bool failOnDiff)
{
    var diffQuiet = await RunCommandAsync("git", ["diff", "--quiet", "--", "."], workingDirectory, allowFailure: true, echoOutput: false);
    if (diffQuiet.ExitCode == 0)
    {
        WriteOutput("diff-found", "false");
        await File.WriteAllTextAsync(Path.Combine(artifactsPath, "changed-files.txt"), string.Empty);
        Console.WriteLine("CleanupCode produced no Git diff.");
        await AppendStepSummaryAsync("""
            ## JetBrains CleanupCode

            CleanupCode produced no Git diff.

            """);
        return 0;
    }

    if (diffQuiet.ExitCode != 1)
    {
        Console.Error.WriteLine($"git diff --quiet failed with exit code {diffQuiet.ExitCode}.");
        return diffQuiet.ExitCode;
    }

    WriteOutput("diff-found", "true");
    var changedFiles = await RunCommandAsync("git", ["diff", "--name-status", "--", "."], workingDirectory, allowFailure: false);
    var diffStat = await RunCommandAsync("git", ["diff", "--stat", "--", "."], workingDirectory, allowFailure: false);
    var fullDiff = await RunCommandAsync("git", ["diff", "--", "."], workingDirectory, allowFailure: false, echoOutput: false);
    var preview = TakeLines(fullDiff.StandardOutput, diffPreviewLines);

    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "changed-files.txt"), changedFiles.StandardOutput);
    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "diff-stat.txt"), diffStat.StandardOutput);
    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "diff-preview.patch"), preview);

    Console.WriteLine("::group::Applied CleanupCode changes");
    Console.WriteLine(changedFiles.StandardOutput.TrimEnd());
    Console.WriteLine(preview.TrimEnd());
    Console.WriteLine("::endgroup::");

    await AppendStepSummaryAsync($"""
        ## JetBrains CleanupCode diff

        {remediationMessage}

        ~~~text
        {changedFiles.StandardOutput.TrimEnd()}
        ~~~

        Applied changes (first {diffPreviewLines} diff lines):

        ~~~diff
        {preview.TrimEnd()}
        ~~~

        """);

    if (failOnDiff)
    {
        Console.Error.WriteLine("CleanupCode produced a Git diff.");
        return 1;
    }

    return 0;
}

static async Task<CommandRunResult> RunCommandAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool allowFailure,
    bool echoOutput = true)
{
    Console.WriteLine(RenderCommand(executable, arguments));

    var result = await Cli.Wrap(executable)
        .WithArguments(arguments)
        .WithWorkingDirectory(workingDirectory)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    if (echoOutput)
    {
        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
    }

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

static int ParsePositiveInteger(string name, int defaultValue)
{
    var rawValue = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        return defaultValue;
    }

    if (!int.TryParse(rawValue, out var value) || value < 1)
    {
        throw new InvalidOperationException($"{name} must be a positive integer.");
    }

    return value;
}

static string ResolvePath(string path, string basePath)
{
    return Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(Path.Combine(basePath, path));
}

static string? ResolveToolExecutable(string toolPath, string commandName)
{
    foreach (var candidate in new[]
    {
        Path.Combine(toolPath, commandName),
        Path.Combine(toolPath, commandName + ".exe")
    })
    {
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
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

static string NullWhenEmpty(string value)
{
    return string.IsNullOrWhiteSpace(value) ? "none" : value;
}

static string TakeLines(string value, int count)
{
    var normalized = value.ReplaceLineEndings("\n");
    return string.Join(Environment.NewLine, normalized.Split('\n').Take(count));
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

/// <summary>
/// JetBrains.ReSharper.GlobalTools version installed when install-tool is true and tool-version is empty.
/// </summary>
/// <remarks>
/// Pinned so a new ReSharper release cannot change CI behavior without a reviewed bump (ARK-745).
/// Renovate updates it through the custom manager in renovate.jsonc, which keys on the renovate: marker comment directly above the constant.
/// </remarks>
static class ToolDefaults
{
    // renovate: datasource=nuget depName=JetBrains.ReSharper.GlobalTools
    public const string ReSharperGlobalTools = "2026.3.0.1";
}

sealed record CommandRunResult(int ExitCode, string StandardOutput, string StandardError);

sealed record ToolInstallResult(int ExitCode, string ExecutablePath);
