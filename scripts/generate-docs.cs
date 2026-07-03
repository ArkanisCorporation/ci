#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property Nullable=enable

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string StartMarker = "<!-- generated:workflow-inputs:start -->";
const string EndMarker = "<!-- generated:workflow-inputs:end -->";

var checkOnly = args.Contains("--check", StringComparer.Ordinal);
var repoRoot = FindRepositoryRoot();
Directory.SetCurrentDirectory(repoRoot);

var catalogPath = Path.Combine(repoRoot, "docs", "workflow-catalog.md");
var schemaRoot = Path.Combine(repoRoot, "schemas", "workflow-inputs");

if (!Directory.Exists(schemaRoot))
{
    return Fail($"{schemaRoot}: schema directory does not exist.");
}

if (!File.Exists(catalogPath))
{
    return Fail($"{catalogPath}: workflow catalog does not exist.");
}

var catalogText = File.ReadAllText(catalogPath);
var newline = DetectNewline(catalogText);
var generated = GenerateWorkflowInputTables(schemaRoot, newline);
var updatedCatalog = ReplaceGeneratedSection(catalogText, generated, newline);

if (checkOnly)
{
    if (!string.Equals(catalogText, updatedCatalog, StringComparison.Ordinal))
    {
        return Fail($"{catalogPath}: generated workflow input docs are stale. Run `dotnet run --file scripts/generate-docs.cs`.");
    }

    Console.WriteLine("Generated workflow input docs are current.");
    return 0;
}

if (string.Equals(catalogText, updatedCatalog, StringComparison.Ordinal))
{
    Console.WriteLine("Generated workflow input docs are already current.");
    return 0;
}

File.WriteAllText(catalogPath, updatedCatalog, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
Console.WriteLine($"Updated {Path.GetRelativePath(repoRoot, catalogPath)}.");
return 0;

static string GenerateWorkflowInputTables(string schemaRoot, string newline)
{
    var builder = new StringBuilder();
    var schemas = Directory.EnumerateFiles(schemaRoot, "wf-*.schema.json")
        .Order(StringComparer.Ordinal)
        .ToArray();

    if (schemas.Length == 0)
    {
        throw new InvalidOperationException($"{schemaRoot}: no workflow input schemas found.");
    }

    foreach (var schemaPath in schemas)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });

        var root = document.RootElement;
        var workflowName = Path.GetFileName(schemaPath).Replace(".schema.json", ".yml", StringComparison.Ordinal);
        var required = ReadRequiredInputs(root);
        var properties = root.GetProperty("properties").EnumerateObject().ToArray();

        builder.Append("### ").Append(workflowName).Append(newline);
        builder.Append(newline);
        builder.Append("Schema: `schemas/workflow-inputs/")
            .Append(Path.GetFileName(schemaPath))
            .Append("`.").Append(newline);
        builder.Append(newline);
        builder.Append("| Input | Type | Required | Default | Details |").Append(newline);
        builder.Append("|---|---|---|---|---|").Append(newline);

        foreach (var property in properties)
        {
            var input = property.Name;
            var definition = property.Value;
            builder.Append("| `").Append(EscapeInlineCode(input)).Append("` | ")
                .Append(EscapeTableText(ReadType(definition))).Append(" | ")
                .Append(required.Contains(input) ? "yes" : "no").Append(" | ")
                .Append(FormatDefault(definition)).Append(" | ")
                .Append(EscapeTableText(ReadDetails(definition))).Append(" |")
                .Append(newline);
        }

        builder.Append(newline);
        builder.Append("Outputs: schema does not define workflow outputs.").Append(newline);
        builder.Append(newline);
    }

    return builder.ToString().TrimEnd() + newline;
}

static HashSet<string> ReadRequiredInputs(JsonElement root)
{
    var required = new HashSet<string>(StringComparer.Ordinal);
    if (!root.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
    {
        return required;
    }

    foreach (var item in requiredElement.EnumerateArray())
    {
        if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
        {
            required.Add(value);
        }
    }

    return required;
}

static string ReadType(JsonElement definition)
{
    if (definition.TryGetProperty("type", out var typeElement))
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            return typeElement.GetString() ?? "unspecified";
        }

        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            return string.Join("/", typeElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()));
        }
    }

    return definition.TryGetProperty("enum", out _) ? "enum" : "unspecified";
}

static string ReadDetails(JsonElement definition)
{
    var parts = new List<string>();
    if (definition.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
    {
        var value = description.GetString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value);
        }
    }

    if (definition.TryGetProperty("enum", out var enumElement) && enumElement.ValueKind == JsonValueKind.Array)
    {
        var values = enumElement.EnumerateArray()
            .Select(FormatJsonValue)
            .ToArray();
        parts.Add($"Allowed: {string.Join(", ", values)}");
    }

    if (definition.TryGetProperty("minimum", out var minimum))
    {
        parts.Add($"Minimum: {minimum.GetRawText()}");
    }

    if (definition.TryGetProperty("maximum", out var maximum))
    {
        parts.Add($"Maximum: {maximum.GetRawText()}");
    }

    return parts.Count == 0 ? "n/a" : string.Join("<br>", parts);
}

static string FormatDefault(JsonElement definition)
{
    if (!definition.TryGetProperty("default", out var defaultElement))
    {
        return "none";
    }

    return $"`{EscapeInlineCode(FormatJsonValue(defaultElement))}`";
}

static string FormatJsonValue(JsonElement element)
{
    return element.ValueKind switch
    {
        JsonValueKind.String => FormatJsonString(element.GetString()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => element.GetRawText()
    };
}

static string FormatJsonString(string? value)
{
    if (value is null)
    {
        return "null";
    }

    var builder = new StringBuilder("\"");
    foreach (var character in value)
    {
        builder.Append(character switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\b' => "\\b",
            '\f' => "\\f",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(character) => $"\\u{(int)character:x4}",
            _ => character
        });
    }

    builder.Append('"');
    return builder.ToString();
}

static string ReplaceGeneratedSection(string catalogText, string generated, string newline)
{
    var replacement = string.Join(newline, [StartMarker, generated.TrimEnd('\r', '\n'), EndMarker]);
    var startIndex = catalogText.IndexOf(StartMarker, StringComparison.Ordinal);
    var endIndex = catalogText.IndexOf(EndMarker, StringComparison.Ordinal);

    if (startIndex >= 0 || endIndex >= 0)
    {
        if (startIndex < 0 || endIndex < 0 || endIndex < startIndex)
        {
            throw new InvalidOperationException("Generated workflow input markers are unbalanced in docs/workflow-catalog.md.");
        }

        var endAfterMarker = endIndex + EndMarker.Length;
        return catalogText[..startIndex] + replacement + catalogText[endAfterMarker..];
    }

    var diagramStyleHeading = Regex.Match(catalogText, @"(?m)^## Diagram Style\s*$");
    if (!diagramStyleHeading.Success)
    {
        throw new InvalidOperationException("Could not find '## Diagram Style' insertion point in docs/workflow-catalog.md.");
    }

    var introduction = string.Join(newline,
    [
        "## Schema-Backed Workflow Inputs",
        string.Empty,
        "The following tables are generated from `schemas/workflow-inputs/*.schema.json`.",
        "Run `dotnet run --file scripts/generate-docs.cs` after schema changes.",
        string.Empty,
        replacement,
        string.Empty,
        string.Empty
    ]);

    return catalogText[..diagramStyleHeading.Index] + introduction + catalogText[diagramStyleHeading.Index..];
}

static string DetectNewline(string text)
{
    return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}

static string EscapeTableText(string value)
{
    return value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r\n", "<br>", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);
}

static string EscapeInlineCode(string value)
{
    return value.Replace("`", "\\`", StringComparison.Ordinal);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    return 1;
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
