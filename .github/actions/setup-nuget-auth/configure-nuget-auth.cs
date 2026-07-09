#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/*
 * Summary:
 *   Converts NUGET_AUTH_JSON into NuGet environment credentials or a temporary Docker BuildKit NuGet.Config secret.
 *
 * Remarks:
 *   The script is intentionally environment-driven so secret JSON never appears in process arguments.
 *   1Password op:// references are not resolved here directly; the prepare phase writes an env template for
 *   1password/load-secrets-action@v4, and the apply phase reads the generated NUGET_AUTH_OP_* variables.
 */

try
{
    var command = AuthCommand.FromEnvironment();
    switch (command.Phase)
    {
        case "prepare":
            Prepare(command);
            break;
        case "apply":
            Apply(command);
            break;
        case "cleanup":
            Cleanup(command);
            break;
        default:
            throw new InvalidOperationException("phase must be one of prepare, apply, or cleanup.");
    }

    return 0;
}
catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException)
{
    Console.Error.WriteLine($"::error::{EscapeCommandValue(exception.Message)}");
    return 1;
}

static void Prepare(AuthCommand command)
{
    var document = ParseDocument(command.AuthJson);
    var opEntries = BuildOpEntries(document).ToArray();

    WriteCommonOutputs(document);
    WriteOutput("configured", "false");
    WriteOutput("op-required", opEntries.Length > 0 ? "true" : "false");

    if (opEntries.Length == 0)
    {
        WriteOutput("op-env-file", string.Empty);
        WriteOutput("op-map-file", string.Empty);
        return;
    }

    var opEnvFile = RequireRunnerTempPath(command.OpEnvFile, "op-env-file");
    var opMapFile = RequireRunnerTempPath(command.OpMapFile, "op-map-file");
    Directory.CreateDirectory(Path.GetDirectoryName(opEnvFile)!);
    Directory.CreateDirectory(Path.GetDirectoryName(opMapFile)!);

    File.WriteAllLines(opEnvFile, opEntries.Select(entry => $"{entry.EnvName}={entry.Reference}"), Utf8NoBom());
    File.WriteAllText(opMapFile, JsonSerializer.Serialize(new OpMap(opEntries), JsonOptions.Indented), Utf8NoBom());

    WriteOutput("op-env-file", opEnvFile);
    WriteOutput("op-map-file", opMapFile);
}

static void Apply(AuthCommand command)
{
    var document = ParseDocument(command.AuthJson);
    var credentials = ResolveCredentials(document, command).ToArray();
    var includesEnv = ModeIncludes(command.CredentialMode, "env");
    var includesDockerConfig = ModeIncludes(command.CredentialMode, "docker-config");

    if (!includesEnv && !includesDockerConfig)
    {
        throw new InvalidOperationException("credential-mode must be one of env, docker-config, or both.");
    }

    foreach (var credential in credentials)
    {
        Mask(credential.Password);
        Mask(credential.EnvironmentValue);
    }

    if (includesEnv)
    {
        foreach (var credential in credentials)
        {
            if (credential.Username.Contains(';', StringComparison.Ordinal)
                || credential.Password.Contains(';', StringComparison.Ordinal)
                || credential.ValidAuthenticationTypes.Contains(';', StringComparison.Ordinal)
                || credential.Username.Contains('\n', StringComparison.Ordinal)
                || credential.Password.Contains('\n', StringComparison.Ordinal)
                || credential.ValidAuthenticationTypes.Contains('\n', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"source {credential.Name} contains characters that cannot be represented safely in NuGetPackageSourceCredentials environment variables.");
            }

            AppendGitHubEnv($"NuGetPackageSourceCredentials_{credential.Name}", credential.EnvironmentValue);
        }
    }

    if (includesDockerConfig)
    {
        var dockerConfigPath = RequireRunnerTempPath(command.DockerConfigPath, "docker-config-path");
        Directory.CreateDirectory(Path.GetDirectoryName(dockerConfigPath)!);
        File.WriteAllText(dockerConfigPath, BuildDockerConfig(credentials), Utf8NoBom());
        WriteOutput("docker-config-path", dockerConfigPath);
    }
    else
    {
        WriteOutput("docker-config-path", string.Empty);
    }

    WriteCommonOutputs(document);
    WriteOutput("configured", "true");
    WriteOutput("op-required", BuildOpEntries(document).Any() ? "true" : "false");
    WriteOutput("op-env-file", command.OpEnvFile);
    WriteOutput("op-map-file", command.OpMapFile);
}

static void Cleanup(AuthCommand command)
{
    var opVariableNames = ReadOpVariableNames(command.OpMapFile).ToArray();

    foreach (var path in new[] { command.OpEnvFile, command.OpMapFile, command.DockerConfigPath })
    {
        DeleteRunnerTempFile(path);
    }

    foreach (var sourceName in SplitCsv(command.ConfiguredSourceNames))
    {
        ValidateSourceName(sourceName);
        AppendGitHubEnv($"NuGetPackageSourceCredentials_{sourceName}", string.Empty);
    }

    foreach (var opVariable in opVariableNames)
    {
        AppendGitHubEnv(opVariable, string.Empty);
    }

    WriteOutput("configured", "false");
    WriteOutput("op-required", "false");
    WriteOutput("source-count", "0");
    WriteOutput("source-names", string.Empty);
    WriteOutput("op-env-file", string.Empty);
    WriteOutput("op-map-file", string.Empty);
    WriteOutput("docker-config-path", string.Empty);
}

static NuGetAuthDocument ParseDocument(string authJson)
{
    if (string.IsNullOrWhiteSpace(authJson))
    {
        throw new InvalidOperationException("nuget-auth-json is required for prepare and apply phases.");
    }

    var document = JsonSerializer.Deserialize<NuGetAuthDocument>(authJson, JsonOptions.Strict)
        ?? throw new InvalidOperationException("nuget-auth-json must be a JSON object.");

    if (document.Version != 1)
    {
        throw new InvalidOperationException("NUGET_AUTH_JSON version must be 1.");
    }

    if (document.Sources.Count == 0)
    {
        throw new InvalidOperationException("NUGET_AUTH_JSON sources must contain at least one source.");
    }

    var names = new HashSet<string>(StringComparer.Ordinal);
    for (var index = 0; index < document.Sources.Count; index++)
    {
        var source = document.Sources[index];
        if (string.IsNullOrWhiteSpace(source.Name))
        {
            throw new InvalidOperationException($"source at index {index} must define name.");
        }

        ValidateSourceName(source.Name);
        if (!names.Add(source.Name))
        {
            throw new InvalidOperationException($"source {source.Name} is duplicated.");
        }

        if (string.IsNullOrWhiteSpace(source.Username))
        {
            throw new InvalidOperationException($"source {source.Name} must define username.");
        }

        if (string.IsNullOrWhiteSpace(source.Password))
        {
            throw new InvalidOperationException($"source {source.Name} must define password.");
        }
    }

    return document;
}

static IEnumerable<ResolvedCredential> ResolveCredentials(NuGetAuthDocument document, AuthCommand command)
{
    var includesDockerConfig = ModeIncludes(command.CredentialMode, "docker-config");
    for (var index = 0; index < document.Sources.Count; index++)
    {
        var source = document.Sources[index];
        if (includesDockerConfig && string.IsNullOrWhiteSpace(source.Source))
        {
            throw new InvalidOperationException($"source {source.Name} must define source when credential-mode includes docker-config.");
        }

        var username = ResolveValue(source.Username, source.Name, index, "username", command);
        var password = ResolveValue(source.Password, source.Name, index, "password", command);
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException($"source {source.Name} resolved username is empty.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException($"source {source.Name} resolved password is empty.");
        }

        var validAuthenticationTypes = source.ValidAuthenticationTypes ?? string.Empty;
        var environmentValue = string.IsNullOrWhiteSpace(validAuthenticationTypes)
            ? $"Username={username};Password={password}"
            : $"Username={username};Password={password};ValidAuthenticationTypes={validAuthenticationTypes}";

        yield return new ResolvedCredential(
            source.Name,
            source.Source ?? string.Empty,
            username,
            password,
            validAuthenticationTypes,
            source.ProtocolVersion ?? string.Empty,
            environmentValue);
    }
}

static string ResolveValue(string value, string sourceName, int sourceIndex, string field, AuthCommand command)
{
    if (value.StartsWith("op://", StringComparison.Ordinal))
    {
        var envName = BuildOpEnvName(sourceIndex, field);
        var resolved = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new InvalidOperationException($"source {sourceName} field {field} uses {value}, but {envName} was not loaded from 1Password.");
        }

        return resolved;
    }

    if (string.Equals(value, "github://actor", StringComparison.Ordinal))
    {
        return command.GitHubActor;
    }

    if (string.Equals(value, "github://token", StringComparison.Ordinal))
    {
        if (string.IsNullOrWhiteSpace(command.GitHubTokenForNuGetAuth))
        {
            throw new InvalidOperationException("github://token requires GITHUB_TOKEN_FOR_NUGET_AUTH.");
        }

        return command.GitHubTokenForNuGetAuth;
    }

    if (value.StartsWith("github://", StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"unsupported GitHub auth reference {value}.");
    }

    return value;
}

static IEnumerable<OpMapEntry> BuildOpEntries(NuGetAuthDocument document)
{
    for (var index = 0; index < document.Sources.Count; index++)
    {
        var source = document.Sources[index];
        if (source.Username.StartsWith("op://", StringComparison.Ordinal))
        {
            yield return new OpMapEntry(source.Name, "username", BuildOpEnvName(index, "username"), source.Username);
        }

        if (source.Password.StartsWith("op://", StringComparison.Ordinal))
        {
            yield return new OpMapEntry(source.Name, "password", BuildOpEnvName(index, "password"), source.Password);
        }
    }
}

static IEnumerable<string> ReadOpVariableNames(string opMapFile)
{
    if (string.IsNullOrWhiteSpace(opMapFile) || !File.Exists(opMapFile))
    {
        return [];
    }

    try
    {
        var fullPath = RequireRunnerTempPath(opMapFile, "op-map-file");
        var map = JsonSerializer.Deserialize<OpMap>(File.ReadAllText(fullPath), JsonOptions.Strict);
        return map?.Entries.Select(entry => entry.EnvName).Where(IsGeneratedOpEnvName).Distinct(StringComparer.Ordinal).ToArray() ?? [];
    }
    catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException)
    {
        Console.Error.WriteLine($"::warning::{EscapeCommandValue($"could not read op-map-file during cleanup: {exception.Message}")}");
        return [];
    }
}

static string BuildOpEnvName(int sourceIndex, string field) =>
    $"NUGET_AUTH_OP_S{sourceIndex + 1}_{field.ToUpperInvariant()}";

static bool IsGeneratedOpEnvName(string value) =>
    Regex.IsMatch(value, @"^NUGET_AUTH_OP_S[0-9]+_(USERNAME|PASSWORD)$", RegexOptions.CultureInvariant);

static bool ModeIncludes(string credentialMode, string requestedMode) =>
    string.Equals(credentialMode, requestedMode, StringComparison.Ordinal)
    || string.Equals(credentialMode, "both", StringComparison.Ordinal);

static void WriteCommonOutputs(NuGetAuthDocument document)
{
    WriteOutput("source-count", document.Sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    WriteOutput("source-names", string.Join(',', document.Sources.Select(source => source.Name)));
}

static string BuildDockerConfig(IReadOnlyCollection<ResolvedCredential> credentials)
{
    var builder = new StringBuilder();
    builder.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
    builder.AppendLine("<configuration>");
    builder.AppendLine("  <packageSources>");
    builder.AppendLine("    <clear />");
    foreach (var credential in credentials)
    {
        var protocolVersion = string.IsNullOrWhiteSpace(credential.ProtocolVersion)
            ? string.Empty
            : $" protocolVersion=\"{XmlEscape(credential.ProtocolVersion)}\"";
        builder
            .Append("    <add key=\"")
            .Append(XmlEscape(credential.Name))
            .Append("\" value=\"")
            .Append(XmlEscape(credential.Source))
            .Append('"')
            .Append(protocolVersion)
            .AppendLine(" />");
    }

    builder.AppendLine("  </packageSources>");
    builder.AppendLine("  <packageSourceCredentials>");
    foreach (var credential in credentials)
    {
        builder.Append("    <").Append(credential.Name).AppendLine(">");
        builder
            .Append("      <add key=\"Username\" value=\"")
            .Append(XmlEscape(credential.Username))
            .AppendLine("\" />");
        builder
            .Append("      <add key=\"ClearTextPassword\" value=\"")
            .Append(XmlEscape(credential.Password))
            .AppendLine("\" />");
        if (!string.IsNullOrWhiteSpace(credential.ValidAuthenticationTypes))
        {
            builder
                .Append("      <add key=\"ValidAuthenticationTypes\" value=\"")
                .Append(XmlEscape(credential.ValidAuthenticationTypes))
                .AppendLine("\" />");
        }

        builder.Append("    </").Append(credential.Name).AppendLine(">");
    }

    builder.AppendLine("  </packageSourceCredentials>");
    builder.AppendLine("</configuration>");
    return builder.ToString();
}

static string XmlEscape(string value) =>
    value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

static void ValidateSourceName(string sourceName)
{
    if (!Regex.IsMatch(sourceName, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
    {
        throw new InvalidOperationException($"source name {sourceName} must match ^[A-Za-z_][A-Za-z0-9_]*$.");
    }
}

static string RequireRunnerTempPath(string path, string inputName)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException($"{inputName} is required.");
    }

    var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
    if (string.IsNullOrWhiteSpace(runnerTemp))
    {
        throw new InvalidOperationException("RUNNER_TEMP is required.");
    }

    var fullPath = Path.GetFullPath(path);
    var tempPath = Path.GetFullPath(runnerTemp);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var tempPrefix = tempPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullPath.Equals(tempPath, comparison) && !fullPath.StartsWith(tempPrefix, comparison))
    {
        throw new InvalidOperationException($"{inputName} must be under RUNNER_TEMP.");
    }

    return fullPath;
}

static void DeleteRunnerTempFile(string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    var fullPath = RequireRunnerTempPath(path, "temporary auth file");
    if (File.Exists(fullPath))
    {
        File.Delete(fullPath);
    }
}

static void AppendGitHubEnv(string name, string value)
{
    var githubEnv = Environment.GetEnvironmentVariable("GITHUB_ENV");
    if (string.IsNullOrWhiteSpace(githubEnv))
    {
        return;
    }

    File.AppendAllText(githubEnv, $"{name}={value}{Environment.NewLine}", Utf8NoBom());
}

static void WriteOutput(string name, string value)
{
    var githubOutput = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (string.IsNullOrWhiteSpace(githubOutput))
    {
        Console.WriteLine($"{name}={value}");
        return;
    }

    File.AppendAllText(githubOutput, $"{name}={value}{Environment.NewLine}", Utf8NoBom());
}

static void Mask(string value)
{
    if (!string.IsNullOrEmpty(value))
    {
        Console.WriteLine($"::add-mask::{EscapeCommandValue(value)}");
    }
}

static Encoding Utf8NoBom() => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

static string EscapeCommandValue(string value) =>
    value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("\r", "%0D", StringComparison.Ordinal)
        .Replace("\n", "%0A", StringComparison.Ordinal);

static IEnumerable<string> SplitCsv(string value) =>
    value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

sealed record AuthCommand(
    string AuthJson,
    string Phase,
    string CredentialMode,
    string OpEnvFile,
    string OpMapFile,
    string DockerConfigPath,
    string ConfiguredSourceNames,
    string GitHubActor,
    string GitHubTokenForNuGetAuth)
{
    public static AuthCommand FromEnvironment() =>
        new(
            GetEnv("NUGET_AUTH_JSON_INPUT"),
            GetEnv("NUGET_AUTH_PHASE", "apply"),
            GetEnv("NUGET_AUTH_CREDENTIAL_MODE", "env"),
            GetEnv("NUGET_AUTH_OP_ENV_FILE"),
            GetEnv("NUGET_AUTH_OP_MAP_FILE"),
            GetEnv("NUGET_AUTH_DOCKER_CONFIG_PATH"),
            GetEnv("NUGET_AUTH_CONFIGURED_SOURCE_NAMES"),
            GetEnv("GITHUB_ACTOR"),
            GetEnv("GITHUB_TOKEN_FOR_NUGET_AUTH"));

    private static string GetEnv(string name, string defaultValue = "") =>
        Environment.GetEnvironmentVariable(name) ?? defaultValue;
}

sealed class NuGetAuthDocument
{
    public int Version { get; init; }

    public List<NuGetAuthSource> Sources { get; init; } = [];
}

sealed class NuGetAuthSource
{
    public string Name { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string? ValidAuthenticationTypes { get; init; }

    public string? ProtocolVersion { get; init; }
}

sealed record ResolvedCredential(
    string Name,
    string Source,
    string Username,
    string Password,
    string ValidAuthenticationTypes,
    string ProtocolVersion,
    string EnvironmentValue);

sealed record OpMap(IReadOnlyList<OpMapEntry> Entries);

sealed record OpMapEntry(string SourceName, string Field, string EnvName, string Reference);

static class JsonOptions
{
    public static readonly JsonSerializerOptions Strict = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
    };
}
