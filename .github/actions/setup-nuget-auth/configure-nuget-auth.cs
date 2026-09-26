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
using System.Xml;
using System.Xml.Linq;

/*
 * Summary:
 *   Converts explicit credentials and opted-in GitHub Packages sources into NuGet restore credentials.
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
catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException or XmlException or ArgumentException)
{
    Console.Error.WriteLine($"::error::{EscapeCommandValue(exception.Message)}");
    return 1;
}

static void Prepare(AuthCommand command)
{
    var document = ParseDocument(command);
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
    var document = ParseDocument(command);
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

    var usesHostConfig = includesEnv && (command.GitHubPackagesAuth || credentials.Any(credential => !IsEnvironmentSourceName(credential.Name)
        || !IsEnvironmentCredentialValue(credential)));
    if (usesHostConfig)
    {
        var hostConfigPath = RequireRunnerTempPath(command.HostConfigPath, "host-config-path");
        Directory.CreateDirectory(Path.GetDirectoryName(hostConfigPath)!);
        var sourceConfigPath = FindSourceConfig(command.SourceConfigPath);
        var hostConfig = BuildHostConfig(sourceConfigPath, credentials);
        WritePrivateFile(hostConfigPath, stream => hostConfig.Save(stream));
        AppendGitHubEnv("RestoreConfigFile", hostConfigPath);
        WriteOutput("host-config-path", hostConfigPath);
    }
    else
    {
        WriteOutput("host-config-path", string.Empty);
    }

    if (includesEnv && !usesHostConfig)
    {
        foreach (var credential in credentials)
        {
            AppendGitHubEnv($"NuGetPackageSourceCredentials_{credential.Name}", credential.EnvironmentValue);
        }
    }

    if (includesDockerConfig)
    {
        var dockerConfigPath = RequireRunnerTempPath(command.DockerConfigPath, "docker-config-path");
        Directory.CreateDirectory(Path.GetDirectoryName(dockerConfigPath)!);
        var dockerConfig = BuildDockerConfig(credentials);
        WritePrivateFile(dockerConfigPath, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom(), leaveOpen: true);
            writer.Write(dockerConfig);
        });
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

    foreach (var path in new[] { command.OpEnvFile, command.OpMapFile, command.DockerConfigPath, command.HostConfigPath })
    {
        DeleteRunnerTempFile(path);
    }

    var configuredNames = !string.IsNullOrWhiteSpace(command.ConfiguredSourceNamesJson)
        ? JsonSerializer.Deserialize<string[]>(command.ConfiguredSourceNamesJson, JsonOptions.Strict) ?? []
        : SplitCsv(command.ConfiguredSourceNames).ToArray();
    foreach (var sourceName in configuredNames.Where(name => ModeIncludes(command.CredentialMode, "env")
        && string.IsNullOrWhiteSpace(command.HostConfigPath) && IsEnvironmentSourceName(name)))
    {
        ValidateSourceName(sourceName);
        AppendGitHubEnv($"NuGetPackageSourceCredentials_{sourceName}", string.Empty);
    }

    if (!string.IsNullOrWhiteSpace(command.HostConfigPath))
    {
        AppendGitHubEnv("RestoreConfigFile", string.Empty);
    }

    foreach (var opVariable in opVariableNames)
    {
        AppendGitHubEnv(opVariable, string.Empty);
    }

    WriteOutput("configured", "false");
    WriteOutput("op-required", "false");
    WriteOutput("source-count", "0");
    WriteOutput("source-names", string.Empty);
    WriteOutput("source-names-json", "[]");
    WriteOutput("host-config-path", string.Empty);
    WriteOutput("op-env-file", string.Empty);
    WriteOutput("op-map-file", string.Empty);
    WriteOutput("docker-config-path", string.Empty);
}

static NuGetAuthDocument ParseDocument(AuthCommand command)
{
    if (string.IsNullOrWhiteSpace(command.AuthJson) && !command.GitHubPackagesAuth)
    {
        throw new InvalidOperationException("nuget-auth-json or github-packages-auth is required for prepare and apply phases.");
    }

    var document = string.IsNullOrWhiteSpace(command.AuthJson)
        ? new NuGetAuthDocument { Version = 1 }
        : JsonSerializer.Deserialize<NuGetAuthDocument>(command.AuthJson, JsonOptions.Strict)
            ?? throw new InvalidOperationException("nuget-auth-json must be a JSON object.");

    if (document.Version != 1)
    {
        throw new InvalidOperationException("NUGET_AUTH_JSON version must be 1.");
    }

    if (command.GitHubPackagesAuth)
    {
        if (command.UntrustedFork)
        {
            throw new InvalidOperationException("github-packages-auth is unavailable for fork pull requests.");
        }
        document.Sources.AddRange(DiscoverGitHubPackageSources(command));
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
    var names = document.Sources.Select(source => source.Name).ToArray();
    WriteOutput("source-names", names.All(IsEnvironmentSourceName) ? string.Join(',', names) : string.Empty);
    WriteOutput("source-names-json", JsonSerializer.Serialize(names));
}

static bool IsEnvironmentSourceName(string name) =>
    Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

static bool IsEnvironmentCredentialValue(ResolvedCredential credential) =>
    !new[] { credential.Username, credential.Password, credential.ValidAuthenticationTypes }
        .Any(value => value.Contains(';', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal));

static string FindSourceConfig(string configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        var path = Path.GetFullPath(configuredPath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("source-config-path does not exist.");
        }
        return path;
    }

    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        foreach (var name in new[] { "NuGet.Config", "nuget.config", "NuGet.config" })
        {
            var path = Path.Combine(directory.FullName, name);
            if (File.Exists(path))
            {
                return path;
            }
        }
    }
    throw new InvalidOperationException("A NuGet.Config is required for source names that cannot use environment credentials. Set source-config-path.");
}

static XDocument LoadNuGetConfig(string path)
{
    using var reader = XmlReader.Create(path, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    });
    return XDocument.Load(reader);
}

static XDocument BuildHostConfig(string sourceConfigPath, IReadOnlyCollection<ResolvedCredential> credentials)
{
    var document = LoadNuGetConfig(sourceConfigPath);
    var root = document.Root;
    if (root?.Name.LocalName != "configuration")
    {
        throw new InvalidOperationException("source-config-path must contain a NuGet configuration root.");
    }

    var sources = root.Element("packageSources")?.Elements("add")
        .Select(element => (string?)element.Attribute("key"))
        .ToHashSet(StringComparer.Ordinal) ?? [];
    foreach (var credential in credentials)
    {
        if (!sources.Contains(credential.Name))
        {
            throw new InvalidOperationException($"source {credential.Name} is not present in source-config-path.");
        }
    }

    var credentialsElement = root.Element("packageSourceCredentials");
    if (credentialsElement is null)
    {
        credentialsElement = new XElement("packageSourceCredentials");
        root.Add(credentialsElement);
    }
    foreach (var credential in credentials)
    {
        var encodedName = XmlConvert.EncodeLocalName(credential.Name);
        credentialsElement.Element(encodedName)?.Remove();
        var sourceElement = new XElement(encodedName,
            new XElement("add", new XAttribute("key", "Username"), new XAttribute("value", credential.Username)),
            new XElement("add", new XAttribute("key", "ClearTextPassword"), new XAttribute("value", credential.Password)));
        if (!string.IsNullOrWhiteSpace(credential.ValidAuthenticationTypes))
        {
            sourceElement.Add(new XElement("add", new XAttribute("key", "ValidAuthenticationTypes"), new XAttribute("value", credential.ValidAuthenticationTypes)));
        }
        credentialsElement.Add(sourceElement);
    }
    return document;
}

static IEnumerable<NuGetAuthSource> DiscoverGitHubPackageSources(AuthCommand command)
{
    if (string.IsNullOrWhiteSpace(command.GitHubRepositoryOwner))
    {
        throw new InvalidOperationException("GITHUB_REPOSITORY_OWNER is required for github-packages-auth.");
    }

    var config = LoadNuGetConfig(FindSourceConfig(command.SourceConfigPath));
    if (config.Root?.Name.LocalName != "configuration")
    {
        throw new InvalidOperationException("source-config-path must contain a NuGet configuration root.");
    }

    var effectiveSources = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in config.Root.Element("packageSources")?.Elements() ?? [])
    {
        var name = (string?)source.Attribute("key");
        if (source.Name.LocalName == "clear")
        {
            effectiveSources.Clear();
        }
        else if (source.Name.LocalName == "remove" && name is not null)
        {
            effectiveSources.Remove(name);
        }
        else if (source.Name.LocalName == "add" && name is not null)
        {
            if (!effectiveSources.TryAdd(name, source))
            {
                throw new InvalidOperationException($"duplicate package source key {name} in source-config-path.");
            }
        }
    }

    var matches = new List<NuGetAuthSource>();
    foreach (var source in effectiveSources.Values)
    {
        var name = (string?)source.Attribute("key");
        var url = (string?)source.Attribute("value");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "nuget.pkg.github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || !string.Equals(uri.AbsolutePath, $"/{command.GitHubRepositoryOwner}/index.json", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        matches.Add(new NuGetAuthSource
        {
            Name = name,
            Source = url,
            Username = "github://actor",
            Password = "github://token",
            ValidAuthenticationTypes = "Basic",
            ProtocolVersion = (string?)source.Attribute("protocolVersion"),
        });
    }

    if (matches.Count == 0)
    {
        throw new InvalidOperationException("github-packages-auth requires a package source at https://nuget.pkg.github.com/{GITHUB_REPOSITORY_OWNER}/index.json in the caller NuGet.Config.");
    }
    return matches;
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
        var encodedName = XmlConvert.EncodeLocalName(credential.Name);
        builder.Append("    <").Append(encodedName).AppendLine(">");
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

        builder.Append("    </").Append(encodedName).AppendLine(">");
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
    if (string.IsNullOrWhiteSpace(sourceName))
    {
        throw new InvalidOperationException("source name must not be blank.");
    }
    XmlConvert.VerifyXmlChars(sourceName);
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

static void WritePrivateFile(string path, Action<Stream> write)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
    var options = new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
    };
    if (!OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }
    using var stream = new FileStream(path, options);
    write(stream);
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
    bool GitHubPackagesAuth,
    bool UntrustedFork,
    string GitHubRepositoryOwner,
    string Phase,
    string CredentialMode,
    string OpEnvFile,
    string OpMapFile,
    string DockerConfigPath,
    string HostConfigPath,
    string SourceConfigPath,
    string ConfiguredSourceNames,
    string ConfiguredSourceNamesJson,
    string GitHubActor,
    string GitHubTokenForNuGetAuth)
{
    public static AuthCommand FromEnvironment() =>
        new(
            GetEnv("NUGET_AUTH_JSON_INPUT"),
            string.Equals(GetEnv("NUGET_AUTH_GITHUB_PACKAGES"), "true", StringComparison.OrdinalIgnoreCase),
            string.Equals(GetEnv("NUGET_AUTH_UNTRUSTED_FORK"), "true", StringComparison.OrdinalIgnoreCase),
            GetEnv("GITHUB_REPOSITORY_OWNER"),
            GetEnv("NUGET_AUTH_PHASE", "apply"),
            GetEnv("NUGET_AUTH_CREDENTIAL_MODE", "env"),
            GetEnv("NUGET_AUTH_OP_ENV_FILE"),
            GetEnv("NUGET_AUTH_OP_MAP_FILE"),
            GetEnv("NUGET_AUTH_DOCKER_CONFIG_PATH"),
            GetEnv("NUGET_AUTH_HOST_CONFIG_PATH"),
            GetEnv("NUGET_AUTH_SOURCE_CONFIG_PATH"),
            GetEnv("NUGET_AUTH_CONFIGURED_SOURCE_NAMES"),
            GetEnv("NUGET_AUTH_CONFIGURED_SOURCE_NAMES_JSON"),
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
