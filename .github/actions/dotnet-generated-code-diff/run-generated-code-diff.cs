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
 *   Runs generated-code commands and gates Git diffs under generated paths.
 *
 * Remarks:
 *   The script is intended to run from the dotnet-generated-code-diff composite action.
 *   It uses CliWrap for native command execution.
 *   Command strings are executed through Bash to preserve repository-owned command syntax.
 */

return await RunAsync();

static async Task<int> RunAsync()
{
    var workspace = RequiredEnvironment("GITHUB_WORKSPACE");
    var workingDirectory = ResolvePath(EnvironmentOrDefault("ACTION_INPUT_WORKING_DIRECTORY", "."), workspace);
    var artifactsPath = ResolvePath(EnvironmentOrDefault("ACTION_INPUT_ARTIFACTS_PATH", "artifacts/generated-code"), workspace);
    var commands = SplitLines(RequiredEnvironment("ACTION_INPUT_COMMANDS")).ToArray();
    var generatedPaths = SplitLines(RequiredEnvironment("ACTION_INPUT_GENERATED_PATHS")).ToArray();
    var runInParallel = ParseBoolean("ACTION_INPUT_RUN_COMMANDS_IN_PARALLEL", defaultValue: true);
    var failOnDiff = ParseBoolean("ACTION_INPUT_FAIL_ON_DIFF", defaultValue: true);
    var remediationMessage = EnvironmentOrDefault("ACTION_INPUT_REMEDIATION_MESSAGE", "Regenerate the listed source files locally and commit the resulting changes.");
    var diffPreviewLines = ParsePositiveInteger("ACTION_INPUT_DIFF_PREVIEW_LINES", defaultValue: 300);

    if (commands.Length == 0)
    {
        Console.Error.WriteLine("commands is required.");
        return 2;
    }

    if (generatedPaths.Length == 0)
    {
        Console.Error.WriteLine("generated-paths is required.");
        return 2;
    }

    if (!Directory.Exists(workingDirectory))
    {
        Console.Error.WriteLine($"working-directory does not exist: {workingDirectory}");
        return 1;
    }

    Directory.CreateDirectory(artifactsPath);
    WriteOutput("artifacts-path", artifactsPath);
    WriteOutput("changed-files", Path.Combine(artifactsPath, "changed-files.txt"));

    var commandResults = runInParallel
        ? await RunCommandsInParallelAsync(commands, workingDirectory, artifactsPath)
        : await RunCommandsSequentiallyAsync(commands, workingDirectory, artifactsPath);

    var failedCommand = commandResults.FirstOrDefault(result => result.ExitCode != 0);
    if (failedCommand is not null)
    {
        Console.Error.WriteLine($"Generated-code command failed with exit code {failedCommand.ExitCode}: {failedCommand.Command}");
        return failedCommand.ExitCode;
    }

    return await ReportDiffAsync(workingDirectory, artifactsPath, generatedPaths, remediationMessage, diffPreviewLines, failOnDiff);
}

static async Task<IReadOnlyList<GeneratedCommandResult>> RunCommandsInParallelAsync(
    IReadOnlyList<string> commands,
    string workingDirectory,
    string artifactsPath)
{
    var tasks = commands.Select((command, index) => RunGeneratedCommandAsync(command, index, workingDirectory, artifactsPath));
    return await Task.WhenAll(tasks);
}

static async Task<IReadOnlyList<GeneratedCommandResult>> RunCommandsSequentiallyAsync(
    IReadOnlyList<string> commands,
    string workingDirectory,
    string artifactsPath)
{
    var results = new List<GeneratedCommandResult>();
    for (var index = 0; index < commands.Count; index++)
    {
        var result = await RunGeneratedCommandAsync(commands[index], index, workingDirectory, artifactsPath);
        results.Add(result);
        if (result.ExitCode != 0)
        {
            break;
        }
    }

    return results;
}

static async Task<GeneratedCommandResult> RunGeneratedCommandAsync(
    string command,
    int index,
    string workingDirectory,
    string artifactsPath)
{
    var logPath = Path.Combine(artifactsPath, $"command-{index + 1}.log");
    Console.WriteLine($"Generated-code command {index + 1}: {command}");

    var result = await Cli.Wrap("bash")
        .WithArguments(["-lc", command])
        .WithWorkingDirectory(workingDirectory)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync();

    await File.WriteAllTextAsync(logPath, result.StandardOutput + result.StandardError);
    Console.Write(result.StandardOutput);
    Console.Error.Write(result.StandardError);

    return new GeneratedCommandResult(command, result.ExitCode);
}

static async Task<int> ReportDiffAsync(
    string workingDirectory,
    string artifactsPath,
    IReadOnlyList<string> generatedPaths,
    string remediationMessage,
    int diffPreviewLines,
    bool failOnDiff)
{
    var pathArgs = generatedPaths.Prepend("--").ToArray();
    var diffQuiet = await RunCommandAsync("git", ["diff", "--quiet", .. pathArgs], workingDirectory, allowFailure: true, echoOutput: false);
    var untracked = await RunCommandAsync("git", ["ls-files", "--others", "--exclude-standard", .. pathArgs], workingDirectory, allowFailure: false, echoOutput: false);
    var hasUntracked = !string.IsNullOrWhiteSpace(untracked.StandardOutput);

    if (diffQuiet.ExitCode == 0 && !hasUntracked)
    {
        WriteOutput("diff-found", "false");
        await File.WriteAllTextAsync(Path.Combine(artifactsPath, "changed-files.txt"), string.Empty);
        await AppendStepSummaryAsync("""
            ## .NET generated code

            Generated paths produced no Git diff.

            """);
        return 0;
    }

    if (diffQuiet.ExitCode is not 0 and not 1)
    {
        Console.Error.WriteLine($"git diff --quiet failed with exit code {diffQuiet.ExitCode}.");
        return diffQuiet.ExitCode;
    }

    WriteOutput("diff-found", "true");
    var changedFiles = await RunCommandAsync("git", ["status", "--short", "--", .. generatedPaths], workingDirectory, allowFailure: false);
    var diffStat = await RunCommandAsync("git", ["diff", "--stat", "--", .. generatedPaths], workingDirectory, allowFailure: false);
    var fullDiff = await RunCommandAsync("git", ["diff", "--", .. generatedPaths], workingDirectory, allowFailure: false, echoOutput: false);

    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "changed-files.txt"), changedFiles.StandardOutput);
    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "untracked-files.txt"), untracked.StandardOutput);
    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "diff-stat.txt"), diffStat.StandardOutput);
    await File.WriteAllTextAsync(Path.Combine(artifactsPath, "diff-preview.patch"), TakeLines(fullDiff.StandardOutput, diffPreviewLines));

    await AppendStepSummaryAsync($"""
        ## .NET generated code diff

        {remediationMessage}

        ~~~text
        {changedFiles.StandardOutput.TrimEnd()}
        ~~~

        """);

    if (failOnDiff)
    {
        Console.Error.WriteLine("Generated paths changed.");
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

static IEnumerable<string> SplitLines(string value)
{
    return value.ReplaceLineEndings("\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !line.StartsWith('#'));
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

static string TakeLines(string value, int count)
{
    var normalized = value.ReplaceLineEndings("\n");
    return string.Join(Environment.NewLine, normalized.Split('\n').Take(count));
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

sealed record GeneratedCommandResult(string Command, int ExitCode);
