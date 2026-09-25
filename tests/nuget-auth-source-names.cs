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

    Console.WriteLine("NuGet auth source-name behavior passed.");
    return 0;
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

RunResult Run(string phase, string json, string mode = "env", string namesJson = "", string hostConfigPath = "")
{
    var caseRoot = Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(caseRoot);
    var envFile = Path.Combine(caseRoot, "github-env.txt");
    var outputFile = Path.Combine(caseRoot, "github-output.txt");
    var dockerFile = Path.Combine(caseRoot, "NuGet.Config");
    var sourceConfig = Path.Combine(caseRoot, "source.NuGet.Config");
    if (json.Length > 0)
    {
        var sourceNames = JsonDocument.Parse(json).RootElement.GetProperty("sources").EnumerateArray().Select(source => source.GetProperty("name").GetString()!).ToArray();
        var config = new XDocument(new XElement("configuration",
            new XElement("packageSources", sourceNames.Select((name, index) => new XElement("add",
                new XAttribute("key", name), new XAttribute("value", $"https://feed{index}.example.invalid/v3/index.json")))),
            new XElement("packageSourceMapping", new XElement("packageSource", new XAttribute("key", sourceNames[0]), new XElement("package", new XAttribute("pattern", "Fixture.*"))))));
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
