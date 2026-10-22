using System.Runtime.CompilerServices;
using CodeDocs.Model;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeDocs.Analysis;

public static class WorkspaceLoader
{
    public static Task<SolutionDocument> LoadAsync(BuildOptions options, CancellationToken cancellationToken = default)
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return LoadRegisteredAsync(options, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<SolutionDocument> LoadRegisteredAsync(BuildOptions options, CancellationToken cancellationToken)
    {
        using var workspace = MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = false;
        var diagnostics = new List<DiagnosticDocument>();
        workspace.WorkspaceFailed += (_, e) =>
        {
            lock (diagnostics)
                diagnostics.Add(new(e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? "Error" : "Warning", "MSBUILD", e.Diagnostic.Message));
        };
        Solution solution;
        if (Path.GetExtension(options.Input).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            solution = await workspace.OpenSolutionAsync(options.Input, cancellationToken: cancellationToken);
        else
            solution = (await workspace.OpenProjectAsync(options.Input, cancellationToken: cancellationToken)).Solution;
        if (diagnostics.Any(d => d.Severity == "Error"))
            return new(Path.GetFileNameWithoutExtension(options.Input), Path.GetFileName(options.Input), "0.1.0",
                AnalysisState.Fatal, [], [], [], diagnostics.OrderBy(d => d.Message, StringComparer.Ordinal).ToArray());
        var units = new List<CompilationUnit>();
        var root = Path.GetDirectoryName(options.Input)!;
        foreach (var project in solution.Projects.OrderBy(p => p.FilePath, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            if (project.Language != LanguageNames.CSharp)
            {
                diagnostics.Add(new("Warning", "UNSUPPORTED_LANGUAGE", $"Skipped non-C# project {project.Name}."));
                continue;
            }
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) throw new InvalidOperationException($"Could not construct compilation for {project.Name}.");
            var file = Path.GetRelativePath(root, project.FilePath!).Replace('\\', '/');
            units.Add(new(project.Name, file, compilation));
        }
        if (units.Count == 0) throw new InvalidOperationException("No C# projects could be loaded.");
        return new SymbolAnalyzer(units, root, options.Visibility).Analyze(Path.GetFileName(options.Input), diagnostics);
    }
}

public sealed record CompilationUnit(string Name, string File, Compilation Compilation);
