#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ManagePackageVersionsCentrally=false
#:property RestorePackagesWithLockFile=false
#:property EnableAotAnalyzer=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

#pragma warning disable IL2026 // The selftest runs without trimming.

var repoRoot = Directory.GetCurrentDirectory();
var actionScript = Path.Combine(repoRoot, ".github", "actions", "setup-nuget-auth", "configure-nuget-auth.cs");
var testRoot = Path.Combine(Path.GetTempPath(), "ci-nuget-auth-names-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    var names = new[] { "github.com-ArkanisCorporation", "Test Source", "a,b", "München & Friends", "a=b", "1st/source", "Literal_x0020_Name" };
    var authJson = JsonSerializer.Serialize(new
    {
        version = 1,
        sources = names.Select((name, index) => new
        {
            name,
            source = $"https://feed{index}.example.invalid/v3/index.json",
            username = "fixture-user",
            password = "fixture-password",
            validAuthenticationTypes = "Basic",
        }),
    });

    var prepare = Run("prepare", authJson);
    Check(prepare.ExitCode == 0, "prepare must accept valid NuGet source names", prepare.Stderr);
    var configuredNames = JsonSerializer.Deserialize<string[]>(Output(prepare, "source-names-json"));
    Check(configuredNames is not null && configuredNames.SequenceEqual(names), "source names must survive output encoding");

    var host = Run("apply", authJson, "env");
    Check(host.ExitCode == 0, "host credentials must accept valid NuGet source names", host.Stderr);
    var hostConfigPath = Output(host, "host-config-path");
    Check(File.Exists(hostConfigPath), "host credential config must be written under runner temp");
    var hostConfig = XDocument.Load(hostConfigPath);
    Check(hostConfig.Root!.Element("packageSourceMapping") is not null, "host config must preserve caller source mapping");
    foreach (var name in names)
    {
        var credential = hostConfig.Root!.Element("packageSourceCredentials")!.Elements().Single(x => XmlConvert.DecodeName(x.Name.LocalName) == name);
        Check((string?)credential.Elements("add").Single(x => (string?)x.Attribute("key") == "Username").Attribute("value") == "fixture-user",
            $"host credential missing for {name}");
    }
    Check(File.ReadAllText(host.EnvFile).Contains($"RestoreConfigFile={hostConfigPath}", StringComparison.Ordinal),
        "host config path must be passed to subsequent restore steps");
    var nugetStart = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = repoRoot,
    };
    foreach (var arg in new[] { "nuget", "list", "source", "--configfile", hostConfigPath }) nugetStart.ArgumentList.Add(arg);
    using (var nuget = Process.Start(nugetStart) ?? throw new InvalidOperationException("Could not start NuGet."))
    {
        var output = nuget.StandardOutput.ReadToEnd();
        var error = nuget.StandardError.ReadToEnd();
        nuget.WaitForExit();
        Check(nuget.ExitCode == 0 && names.All(name => output.Contains(name, StringComparison.Ordinal)),
            "NuGet must read every source name from the generated host config", error);
    }

    var docker = Run("apply", authJson, "docker-config");
    Check(docker.ExitCode == 0, "Docker credentials must accept valid NuGet source names", docker.Stderr);
    var config = XDocument.Load(Output(docker, "docker-config-path"));
    foreach (var name in names)
    {
        var source = config.Root!.Element("packageSources")!.Elements("add").Single(x => (string?)x.Attribute("key") == name);
        Check(source is not null, $"Docker source missing for {name}");
        var credential = config.Root.Element("packageSourceCredentials")!.Elements().Single(x => XmlConvert.DecodeName(x.Name.LocalName) == name);
        Check((string?)credential.Elements("add").Single(x => (string?)x.Attribute("key") == "Username").Attribute("value") == "fixture-user",
            $"Docker credential missing for {name}");
    }

    var cleanup = Run("cleanup", string.Empty, namesJson: Output(host, "source-names-json"), hostConfigPath: hostConfigPath);
    Check(cleanup.ExitCode == 0, "cleanup must accept encoded source names", cleanup.Stderr);
    Check(!File.Exists(hostConfigPath), "cleanup must delete host config");
    Check(File.ReadAllText(cleanup.EnvFile).Contains($"RestoreConfigFile={Environment.NewLine}", StringComparison.Ordinal),
        "cleanup must clear restore config override");

    var simpleJson = JsonSerializer.Serialize(new
    {
        version = 1,
        sources = new[] { new { name = "simple_source", source = "https://simple.example.invalid/v3/index.json", username = "fixture-user", password = "fixture-password" } },
    });
    var simple = Run("apply", simpleJson, "env");
    Check(simple.ExitCode == 0, "environment-safe names retain the existing credential path", simple.Stderr);
    Check(File.ReadAllText(simple.EnvFile).Contains("NuGetPackageSourceCredentials_simple_source=Username=fixture-user;Password=fixture-password", StringComparison.Ordinal),
        "environment-safe source credential missing");

    var semicolonJson = JsonSerializer.Serialize(new
    {
        version = 1,
        sources = new[] { new { name = "simple_source", source = "https://simple.example.invalid/v3/index.json", username = "fixture-user", password = "fixture;password" } },
    });
    var semicolon = Run("apply", semicolonJson, "env");
    Check(semicolon.ExitCode == 0 && File.Exists(Output(semicolon, "host-config-path")),
        "credential values unsafe for the environment must use a temporary config", semicolon.Stderr);

    var githubSources = new (string Name, string Url)[]
    {
        ("github.com-ArkanisCorporation", "https://nuget.pkg.github.com/ArkanisCorporation/index.json"),
        ("lookalike", "https://nuget.pkg.github.com.evil.example/ArkanisCorporation/index.json"),
        ("other-owner", "https://nuget.pkg.github.com/OtherOrganization/index.json"),
        ("insecure", "http://nuget.pkg.github.com/ArkanisCorporation/index.json"),
        ("query", "https://nuget.pkg.github.com/ArkanisCorporation/index.json?redirect=evil"),
    };
    var githubPrepare = Run("prepare", "", githubPackages: true, sourceEntries: githubSources);
    Check(githubPrepare.ExitCode == 0, "GitHub Packages opt-in must work without auth JSON", githubPrepare.Stderr);
    Check(Output(githubPrepare, "source-names-json") == "[\"github.com-ArkanisCorporation\"]",
        "GitHub Packages discovery must select only the exact caller-owner endpoint");
    Check(Output(githubPrepare, "op-required") == "false", "GitHub Packages auth must not require 1Password");

    var githubHost = Run("apply", "", githubPackages: true, sourceEntries: githubSources);
    Check(githubHost.ExitCode == 0, "GitHub Packages host auth must work without auth JSON", githubHost.Stderr);
    var githubConfig = XDocument.Load(Output(githubHost, "host-config-path"));
    var githubCredentials = githubConfig.Root!.Element("packageSourceCredentials")!.Elements().ToArray();
    Check(githubCredentials.Length == 1 && XmlConvert.DecodeName(githubCredentials[0].Name.LocalName) == "github.com-ArkanisCorporation",
        "GitHub token must be bound only to the matching source");
    Check((string?)githubCredentials[0].Elements("add").Single(x => (string?)x.Attribute("key") == "Username").Attribute("value") == "fixture-actor",
        "GitHub Packages username must use the workflow actor");
    Check((string?)githubCredentials[0].Elements("add").Single(x => (string?)x.Attribute("key") == "ClearTextPassword").Attribute("value") == "fixture-github-token",
        "GitHub Packages password must use the workflow token");
    Check(githubHost.Stdout.Split('\n').All(line => !line.Contains("fixture-github-token", StringComparison.Ordinal)
            || line.StartsWith("::add-mask::", StringComparison.Ordinal))
        && !githubHost.Stderr.Contains("fixture-github-token", StringComparison.Ordinal),
        "GitHub token must appear only in the GitHub masking command");

    var githubDocker = Run("apply", "", "docker-config", githubPackages: true, sourceEntries: githubSources);
    Check(githubDocker.ExitCode == 0, "GitHub Packages Docker auth must work without auth JSON", githubDocker.Stderr);
    var githubDockerConfig = XDocument.Load(Output(githubDocker, "docker-config-path"));
    Check(githubDockerConfig.Root!.Element("packageSources")!.Elements("add").Count() == 1,
        "Docker secret config must include only the matching GitHub Packages source");

    var githubSimple = Run("apply", "", githubPackages: true,
        sourceEntries: [("github", "https://nuget.pkg.github.com/arkaniscorporation/index.json")]);
    Check(githubSimple.ExitCode == 0, "GitHub Packages auth must support an environment-safe source name", githubSimple.Stderr);
    Check(File.Exists(Output(githubSimple, "host-config-path"))
        && !File.ReadAllText(githubSimple.EnvFile).Contains("NuGetPackageSourceCredentials_github=", StringComparison.Ordinal),
        "GitHub Packages auth must bind the token to the exact config even when the source name is environment-safe");

    var otherFeedJson = JsonSerializer.Serialize(new
    {
        version = 1,
        sources = new[] { new { name = "other_feed", source = "https://other.example.invalid/v3/index.json", username = "fixture-user", password = "fixture-password" } },
    });
    var mixed = Run("prepare", otherFeedJson, githubPackages: true,
        sourceEntries: [githubSources[0], ("other_feed", "https://other.example.invalid/v3/index.json")]);
    Check(mixed.ExitCode == 0 && Output(mixed, "source-count") == "2",
        "GitHub Packages auth must compose with explicit credentials for other feeds", mixed.Stderr);
    var duplicateJson = JsonSerializer.Serialize(new
    {
        version = 1,
        sources = new[] { new { name = "github.com-ArkanisCorporation", source = githubSources[0].Url, username = "fixture-user", password = "fixture-password" } },
    });
    var duplicate = Run("prepare", duplicateJson, githubPackages: true, sourceEntries: [githubSources[0]]);
    Check(duplicate.ExitCode != 0, "duplicate explicit and discovered source names must fail");
    var duplicateConfigKey = Run("prepare", "", githubPackages: true,
        sourceEntries: [("github", githubSources[0].Url), ("github", "https://evil.example.invalid/v3/index.json")]);
    Check(duplicateConfigKey.ExitCode != 0, "duplicate NuGet source keys must fail before binding a token");
    var duplicateConfigKeyCase = Run("prepare", "", githubPackages: true,
        sourceEntries: [("github", githubSources[0].Url), ("GITHUB", "https://evil.example.invalid/v3/index.json")]);
    Check(duplicateConfigKeyCase.ExitCode != 0, "source keys differing only in case must fail before binding a token");
    var replacedSourceConfig = new XDocument(new XElement("configuration", new XElement("packageSources",
        new XElement("add", new XAttribute("key", "github"), new XAttribute("value", githubSources[0].Url)),
        new XElement("remove", new XAttribute("key", "github")),
        new XElement("add", new XAttribute("key", "github"), new XAttribute("value", "https://evil.example.invalid/v3/index.json")))));
    var replacedSource = Run("prepare", "", githubPackages: true, sourceConfigXml: replacedSourceConfig.ToString());
    Check(replacedSource.ExitCode != 0, "removed or replaced GitHub sources must not receive a token");

    var unmatched = Run("prepare", "", githubPackages: true, sourceEntries: githubSources[1..]);
    Check(unmatched.ExitCode != 0, "GitHub Packages opt-in must fail when no exact caller-owner source exists");
    var fork = Run("prepare", "", githubPackages: true, untrustedFork: true, sourceEntries: githubSources);
    Check(fork.ExitCode != 0, "GitHub Packages auth must reject fork pull requests");

    Console.WriteLine("NuGet auth source-name behavior passed.");
    return 0;
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

RunResult Run(string phase, string json, string mode = "env", string namesJson = "", string hostConfigPath = "",
    bool githubPackages = false, bool untrustedFork = false, (string Name, string Url)[]? sourceEntries = null,
    string? sourceConfigXml = null)
{
    var caseRoot = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(caseRoot);
    var envFile = Path.Combine(caseRoot, "github-env.txt");
    var outputFile = Path.Combine(caseRoot, "github-output.txt");
    var dockerFile = Path.Combine(caseRoot, "NuGet.Config");
    var sourceConfig = Path.Combine(caseRoot, "source.NuGet.Config");
    if (sourceConfigXml is not null)
    {
        File.WriteAllText(sourceConfig, sourceConfigXml);
    }
    else if (sourceEntries is not null || json.Length > 0)
    {
        sourceEntries ??= JsonDocument.Parse(json).RootElement.GetProperty("sources").EnumerateArray()
            .Select((source, index) => (source.GetProperty("name").GetString()!, $"https://feed{index}.example.invalid/v3/index.json"))
            .ToArray();
        var config = new XDocument(new XElement("configuration",
            new XElement("packageSources", sourceEntries.Select(source => new XElement("add",
                new XAttribute("key", source.Name), new XAttribute("value", source.Url)))),
            new XElement("packageSourceMapping", new XElement("packageSource", new XAttribute("key", sourceEntries[0].Name), new XElement("package", new XAttribute("pattern", "Fixture.*"))))));
        config.Save(sourceConfig);
    }
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = repoRoot,
    };
    foreach (var arg in new[] { "run", "--file", actionScript, "--no-launch-profile" })
    {
        start.ArgumentList.Add(arg);
    }

    start.Environment["NUGET_AUTH_PHASE"] = phase;
    start.Environment["NUGET_AUTH_JSON_INPUT"] = json;
    start.Environment["NUGET_AUTH_CREDENTIAL_MODE"] = mode;
    start.Environment["NUGET_AUTH_GITHUB_PACKAGES"] = githubPackages ? "true" : "false";
    start.Environment["NUGET_AUTH_UNTRUSTED_FORK"] = untrustedFork ? "true" : "false";
    start.Environment["GITHUB_REPOSITORY_OWNER"] = "ArkanisCorporation";
    start.Environment["GITHUB_ACTOR"] = "fixture-actor";
    start.Environment["GITHUB_TOKEN_FOR_NUGET_AUTH"] = "fixture-github-token";
    start.Environment["NUGET_AUTH_CONFIGURED_SOURCE_NAMES_JSON"] = namesJson;
    start.Environment["NUGET_AUTH_DOCKER_CONFIG_PATH"] = dockerFile;
    start.Environment["NUGET_AUTH_HOST_CONFIG_PATH"] = hostConfigPath.Length > 0 ? hostConfigPath : Path.Combine(caseRoot, "host.NuGet.Config");
    start.Environment["NUGET_AUTH_SOURCE_CONFIG_PATH"] = sourceConfig;
    start.Environment["RUNNER_TEMP"] = testRoot;
    start.Environment["GITHUB_ENV"] = envFile;
    start.Environment["GITHUB_OUTPUT"] = outputFile;
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start auth script.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return new RunResult(process.ExitCode, stdout, stderr, envFile, outputFile);
}

static string Output(RunResult result, string name)
{
    var prefix = name + "=";
    return File.ReadAllLines(result.OutputFile).Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
}

static void Check(bool condition, string message, string detail = "")
{
    if (!condition)
    {
        throw new InvalidOperationException($"{message}: {detail}");
    }
}

sealed record RunResult(int ExitCode, string Stdout, string Stderr, string EnvFile, string OutputFile);
