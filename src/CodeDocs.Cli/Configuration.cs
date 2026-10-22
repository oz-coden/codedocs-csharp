using System.Text.Json;
using System.Text.Json.Serialization;
using CodeDocs.Model;

namespace CodeDocs.Cli;

public sealed class ArgumentExceptionWithUsage(string message) : Exception(message);
public static class Configuration
{
    private sealed record Config
    {
        public string? Input { get; init; }
        public string? Output { get; init; }
        public string[]? Formats { get; init; }
        public string? Visibility { get; init; }
        public AnalysisConfig? Analysis { get; init; }
        public HtmlConfig? Html { get; init; }
        public MarkdownConfig? Markdown { get; init; }
        public SourceConfig? Source { get; init; }
    }
    private sealed record AnalysisConfig { public string? OnIncomplete { get; init; } }
    private sealed record HtmlConfig { public bool Search { get; init; } = true; }
    private sealed record MarkdownConfig { public bool SingleFile { get; init; } }
    private sealed record SourceConfig { public bool ShowDeclarationFiles { get; init; } = true; }

    public static BuildOptions Parse(string[] args, string currentDirectory)
    {
        if (args.Length == 0 || args[0] != "build") throw new ArgumentExceptionWithUsage("Expected: codedocs build [input] [options].");
        Config config = new();
        var configPath = Path.Combine(currentDirectory, "codedocs.json");
        if (File.Exists(configPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath), new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                    ?? throw new ArgumentExceptionWithUsage("codedocs.json must contain an object.");
            }
            catch (JsonException e) { throw new ArgumentExceptionWithUsage("Invalid codedocs.json: " + e.Message); }
        }
        string? input = config.Input;
        var output = config.Output ?? "docs/api";
        var formats = config.Formats?.ToList() ?? ["html"];
        var visibility = config.Visibility ?? "public";
        var incomplete = config.Analysis?.OnIncomplete ?? "keep-existing";
        var search = config.Html?.Search ?? true;
        var single = config.Markdown?.SingleFile ?? false;
        bool verbose = false, clean = false, explicitInput = false, explicitFormats = false;
        for (var i = 1; i < args.Length; i++)
        {
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith('-')) throw new ArgumentExceptionWithUsage($"Missing value for {args[i - 1]}.");
                return args[i];
            }
            switch (args[i])
            {
                case "-o": case "--output": output = Value(); break;
                case "--format":
                    if (!explicitFormats) { formats.Clear(); explicitFormats = true; }
                    formats.Add(Value()); break;
                case "--visibility": visibility = Value(); break;
                case "--on-incomplete": incomplete = Value(); break;
                case "--single-file": single = true; break;
                case "--clean": clean = true; break;
                case "--no-search": search = false; break;
                case "--verbose": verbose = true; break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentExceptionWithUsage("Unknown option: " + args[i]);
                    if (explicitInput) throw new ArgumentExceptionWithUsage("Specify one .sln or .csproj input.");
                    input = args[i]; explicitInput = true; break;
            }
        }
        if (input is null)
        {
            var candidates = Directory.EnumerateFiles(currentDirectory).Where(f => IsInput(f)).Order(StringComparer.Ordinal).ToArray();
            if (candidates.Length != 1)
                throw new ArgumentExceptionWithUsage(candidates.Length == 0 ? "No .sln or .csproj found in the current directory." : "Multiple inputs found; specify one explicitly:\n" + string.Join("\n", candidates.Select(Path.GetFileName)));
            input = candidates[0];
        }
        if (string.IsNullOrWhiteSpace(input) || !IsInput(input)) throw new ArgumentExceptionWithUsage("Input must be a .sln or .csproj file.");
        input = Path.GetFullPath(input, currentDirectory);
        if (!File.Exists(input)) throw new ArgumentExceptionWithUsage("Input does not exist: " + input);
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentExceptionWithUsage("Output directory cannot be empty.");
        if (formats.Count == 0 || formats.Any(f => f is not ("html" or "markdown" or "json")))
            throw new ArgumentExceptionWithUsage("Formats must be html, markdown, or json.");
        if (single && !formats.Contains("markdown")) throw new ArgumentExceptionWithUsage("--single-file requires --format markdown.");
        return new()
        {
            Input = input, Output = Path.GetFullPath(output, currentDirectory), Formats = formats.Distinct().Order(StringComparer.Ordinal).ToArray(),
            Visibility = visibility switch { "public" => Visibility.Public, "api" => Visibility.Api, "non-private" => Visibility.NonPrivate, "all" => Visibility.All, _ => throw new ArgumentExceptionWithUsage("Invalid visibility: " + visibility) },
            OnIncomplete = incomplete switch { "keep-existing" => IncompletePolicy.KeepExisting, "overwrite" => IncompletePolicy.Overwrite, "fail" => IncompletePolicy.Fail, _ => throw new ArgumentExceptionWithUsage("Invalid incomplete policy: " + incomplete) },
            Search = search, SingleFile = single, Clean = clean, Verbose = verbose,
            ShowDeclarationFiles = config.Source?.ShowDeclarationFiles ?? true
        };
    }
    private static bool IsInput(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".csproj";
}
