using CodeDocs.Analysis;
using CodeDocs.Model;

namespace CodeDocs.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args) => await RunAsync(args, Environment.CurrentDirectory, Console.Out, Console.Error);

    public static async Task<int> RunAsync(string[] args, string currentDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args.SequenceEqual(new[] { "--version" })) { await output.WriteLineAsync("CodeDocs.CSharp 0.1.0"); return 0; }
        if (args.SequenceEqual(new[] { "--help" }) || args.SequenceEqual(new[] { "build", "--help" }))
        { await output.WriteLineAsync(Help); return 0; }
        BuildOptions? options = null;
        try
        {
            options = Configuration.Parse(args, currentDirectory);
            var document = await WorkspaceLoader.LoadAsync(options, cancellationToken);
            await output.WriteLineAsync($"Projects: {document.Projects.Count}\nTypes: {document.Types.Count}\nMembers: {document.Types.Sum(t => t.Members.Count)}\nWarnings: {document.Diagnostics.Count(d => d.Severity == "Warning")}\nCompilation errors: {document.Diagnostics.Count(d => d.Severity == "Error")}\nAnalysis: {document.State}");
            foreach (var diagnostic in document.Diagnostics.Where(d => options.Verbose || d.Severity == "Error"))
                await error.WriteLineAsync($"{diagnostic.File}{(diagnostic.Line is null ? "" : ":" + diagnostic.Line)} {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}".TrimStart());
            if (document.State == AnalysisState.Fatal) return 1;
            if (document.State == AnalysisState.Partial && options.OnIncomplete != IncompletePolicy.Overwrite)
            {
                await error.WriteLineAsync(options.OnIncomplete == IncompletePolicy.KeepExisting
                    ? "Incomplete compilation: existing output was kept. Use analysis.onIncomplete=overwrite to publish partial output."
                    : "Compilation errors: generation failed according to analysis.onIncomplete=fail.");
                return 2;
            }
            using var transaction = new OutputTransaction(options);
            await transaction.RenderAsync(document, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            await output.WriteLineAsync("Output: " + options.Output);
            return document.State == AnalysisState.Success ? 0 : 2;
        }
        catch (ArgumentExceptionWithUsage e) { await error.WriteLineAsync(e.Message); return 3; }
        catch (OperationCanceledException) { await error.WriteLineAsync("Generation cancelled; existing output was kept."); return 1; }
        catch (Exception e)
        {
            await error.WriteLineAsync(options?.Verbose == true ? e.ToString() : e.Message);
            return 1;
        }
    }
    private const string Help = """
        CodeDocs.CSharp — compiler-oriented C# API documentation

        codedocs build [input.sln|input.csproj] [options]

        -o, --output <directory>         Output directory (default: docs/api)
        --format <html|markdown|json>    Repeat to select multiple formats
        --visibility <public|api|non-private|all>
        --on-incomplete <keep-existing|overwrite|fail>
        --single-file                   Write Markdown to api.md
        --clean                         Adopt/replace an unmanaged output directory
        --no-search                     Disable HTML search
        --verbose                       Print all diagnostics
        --help                          Show usage
        --version                       Show version

        Reads codedocs.json from the current directory only. CLI values override it.
        Restore the target project with dotnet restore before generating documentation.
        Multiple formats are placed in html/, markdown/, and json/ subdirectories.
        Output is always a complete snapshot; obsolete generated files are removed.
        Exit codes: 0 success; 1 fatal failure; 2 incomplete compilation; 3 invalid arguments/configuration.
        """;
}
