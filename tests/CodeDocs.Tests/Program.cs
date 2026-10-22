using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDocs.Analysis;
using CodeDocs.Cli;
using CodeDocs.Model;
using CodeDocs.Rendering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Cli = CodeDocs.Cli.Program;

var runner = new Suite();
await runner.Run(args.Contains("--unit-only"));

internal sealed class Suite
{
    private readonly string root = FindRoot();
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "codedocs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private int assertions;
    private void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static string Text(SymbolDocument s) => string.Concat(s.Declaration.Select(p => p.Text));
    private CSharpCompilation Compile(string source, string name = "Fixture", string file = "fixture.cs") => CSharpCompilation.Create(name,
        [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13, DocumentationMode.Diagnose), Path.Combine(root, file))], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
    private SolutionDocument Analyze(string source, Visibility visibility = Visibility.Public) => new SymbolAnalyzer([new("Fixture", "Fixture.csproj", Compile(source))], root, visibility).Analyze("Fixture.csproj");
    private SolutionDocument Example() => Analyze(File.ReadAllText(Path.Combine(root, "samples/Example/Api.cs")).Replace("using System.ComponentModel;", "using System; using System.Threading.Tasks; using System.ComponentModel;"));
    private BuildOptions Options(string name, params string[] formats) => new() { Input = Path.Combine(root, "samples/Example/Example.csproj"), Output = Path.Combine(scratch, name), Formats = formats.Length == 0 ? ["html"] : formats };
    public async Task Run(bool unitOnly)
    {
        Directory.CreateDirectory(scratch);
        var tests = new List<(string, Func<Task>)>
        {
            ("C# semantic declarations", () => Sync(Semantics)), ("Effective visibility", () => Sync(VisibilityRules)),
            ("Stable identity and internal references", () => Sync(Identities)), ("XML documentation and escaping", Documentation),
            ("Deterministic renderers and link integrity", Renderers), ("Snapshot transactions and path guards", Transactions),
            ("CLI configuration and discovery", () => Sync(ConfigurationRules)),
            ("Structured semantic information", () => Sync(() => RefinementTests.Semantics(source => Analyze(source, Visibility.All), Check))),
            ("XML selection, remapping and metadata rendering", () => RefinementTests.Documentation(source => Analyze(source, Visibility.All), Check, scratch))
        };
        if (!unitOnly) tests.Add(("MSBuild integration and incomplete policies", Integration));
        var failed = 0;
        try
        {
            foreach (var (name, test) in tests)
            {
                try { await test(); Console.WriteLine("PASS " + name); }
                catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + e); }
            }
            Console.WriteLine($"{tests.Count - failed}/{tests.Count} groups passed; {assertions} assertions." + (unitOnly ? " MSBuild integration explicitly excluded." : ""));
        }
        finally { Directory.Delete(scratch, true); }
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
    private static Task Sync(Action a) { a(); return Task.CompletedTask; }
    private void Semantics()
    {
        var doc = Example();
        Check(doc.State == AnalysisState.Success, string.Join("\n", doc.Diagnostics.Where(d => d.Severity == "Error")));
        var repo = doc.Types.Single(t => t.Name == "Repository<T>");
        Check(Text(repo).Contains("Repository<T>(string name)") && Text(repo).Contains("where T : class, IEntity, new()"), Text(repo));
        Check(repo.Declarations.Count == 2 && repo.Members.Any(m => m.Name == "Save"), "Partial declaration/member missing.");
        Check(repo.Members.Any(m => m.IsPrimaryConstructor), "Primary constructor missing.");
        Check(repo.GenericParameters.Single().ConstraintItems.SequenceEqual(new[] { "class", "IEntity", "new()" }), "Generic constraints lost.");
        foreach (var (name, expected) in new[] { ("FindAsync", "Task<(User? user, bool found)>"), ("Echo", "dynamic Echo(dynamic value)"), ("TryGet", "out User? user"), ("Log", "params string[] messages"), ("Connect", "int port = 8080"), ("Name", "get; init;"), ("Count", "private set;"), ("Copy", "byte*") })
            Check(Text(repo.Members.Single(m => m.Name == name)).Contains(expected), name + ": " + Text(repo.Members.Single(m => m.Name == name)));
        Check(Text(repo.Members.Single(m => m.Name == "FindAsync")).StartsWith("public async Task", StringComparison.Ordinal), "Async modifier is not placed after accessibility.");
        Check(Text(repo.Members.Single(m => m.Name == "Copy")).Contains("unsafe"), "Unsafe modifier lost.");
        Check(Text(repo.Members.Single(m => m.Name == "Name")).Contains("required"), "Required modifier lost.");
        foreach (var category in new[] { "Indexers", "Events", "Explicit Interface Implementations" }) Check(repo.Members.Any(m => m.Category == category), category + " missing.");
        var point = doc.Types.Single(t => t.Name == "Point");
        Check(Text(point).Contains("readonly record struct Point(double X, double Y)"), Text(point));
        Check(point.Members.Any(m => m.Category == "Operators") && point.Members.Any(m => m.Category == "Conversions"), "Operator categories lost.");
        Check(point.Members.Any(m => m.Name == "X"), "Synthesized property missing.");
        Check(Text(doc.Types.Single(t => t.Name == "UserFactory")).Contains("delegate"), "Delegate signature missing.");
        Check(Text(doc.Types.Single(t => t.Name == "Status")).Contains(": byte"), "Enum underlying type missing.");
        Check(Text(doc.Types.Single(t => t.Name == "IProducer<out T>")).Contains("out T"), "Variance missing.");
        var extension = doc.Types.Single(t => t.Name == "Extensions").Members.Single(m => m.Name == "IsHello");
        Check(extension.IsExtensionMethod && extension.ExtensionTarget?.Display == "string", "Extension target missing.");
        Check(repo.InheritedMembers.Any(m => m.Name.Contains("Stop()") && m.MemberId is not null) && !repo.InheritedMembers.Any(m => m.Name.Contains("Start()")), "Inherited member handling failed.");
        Check(!Text(doc.Types.Single(t => t.Name == "Extensions")).Contains("abstract") && !Text(doc.Types.Single(t => t.Name == "Extensions")).Contains("sealed"), "Static type emitted implicit abstract/sealed modifiers.");
        Check(!Text(doc.Types.Single(t => t.Name == "IRunner")).Contains("abstract interface"), "Interface emitted an invalid implicit abstract modifier.");
        var advanced = Analyze("public ref struct Buffer { public readonly ref readonly int Read(scoped in int x) => ref x; } public unsafe class Native { public volatile int Flag; public delegate* unmanaged[Cdecl]<int, int> Pointer; }");
        Check(Text(advanced.Types.Single(t => t.Name == "Buffer")).Contains("ref struct"), "Ref struct lost.");
        Check(Text(advanced.Types.Single(t => t.Name == "Buffer").Members.Single(m => m.Name == "Read")).Contains("scoped in int"), "Scoped parameter lost.");
        Check(Text(advanced.Types.Single(t => t.Name == "Native").Members.Single(m => m.Name == "Flag")).Contains("volatile"), "Volatile field lost.");
        Check(Text(advanced.Types.Single(t => t.Name == "Native").Members.Single(m => m.Name == "Pointer")).Contains("delegate* unmanaged[Cdecl]<int, int>"), "Function pointer convention lost.");
        Check(repo.Attributes.Count > 0 && repo.Relationships.Any(r => r.Kind == "Attribute"), "Attribute relationship missing.");
        var compilation = Compile("public partial class Generated {}").AddSyntaxTrees(CSharpSyntaxTree.ParseText("// <auto-generated/>\npublic partial class Generated { public void Added() {} }", new CSharpParseOptions(LanguageVersion.CSharp13), Path.Combine(root, "fixture.g.cs")));
        var generated = new SymbolAnalyzer([new("Fixture", "Fixture.csproj", compilation)], root, Visibility.Public).Analyze("Fixture.csproj");
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new GeneratedFixture().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.CSharp13));
        driver.RunGeneratorsAndUpdateCompilation(Compile("public partial class Generated {}"), out var generatedCompilation, out var generatorDiagnostics);
        var fromGenerator = new SymbolAnalyzer([new("Fixture", "Fixture.csproj", generatedCompilation)], root, Visibility.Public).Analyze("Fixture.csproj");
        Check(!generatorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) && fromGenerator.Types.Single().Members.Any(m => m.Name == "FromGenerator"), "Source generator API missing.");
        Check(fromGenerator.Types.Single().Declarations.Where(d => d.Generated).All(d => !d.File.StartsWith("../")), "Generated source path depends on the current directory.");
        var attributed = Analyze("using System; [AttributeUsage(AttributeTargets.All)] public class MarkerAttribute : Attribute {} public class Annotated { public int Value { [Marker] get; [Marker] set; } }");
        Check(attributed.Types.Single(t => t.Name == "Annotated").Members.Single(m => m.Name == "Value").AccessorDetails.All(a => a.Attributes.Any(x => x.Display.Contains("Marker"))), "Accessor attributes lost.");
        Check(generated.Types.Single().Declarations.Any(d => d.Generated) && generated.Types.Single().Members.Any(m => m.Name == "Added"), "Generated API excluded.");
    }
    private void VisibilityRules()
    {
        const string source = """
            internal class Hidden { public class Nested {} }
            public class Outer {
              public void Public() {} protected void Protected() {} protected internal void ProtectedInternal() {}
              private protected void PrivateProtected() {} internal void Internal() {} private void Private() {}
              protected class ProtectedNested {} internal class InternalNested {} private class PrivateNested {}
            }
            file class Local { }
            """;
        var pub = Analyze(source); Check(pub.Types.Count == 1, "Inaccessible container leaked.");
        Check(pub.Types.Single().Members.All(m => m.Name == "Public" || m.Category == "Constructors"), "Non-public members leaked.");
        var api = Analyze(source, Visibility.Api).Types.Single(t => t.Name == "Outer");
        Check(api.Members.Any(m => m.Name == "Protected") && api.Members.Any(m => m.Name == "ProtectedInternal"), "Protected API missing.");
        Check(!api.Members.Any(m => m.Name is "PrivateProtected" or "Internal"), "Assembly-only API leaked.");
        var non = Analyze(source, Visibility.NonPrivate);
        Check(non.Types.Any(t => t.Name == "Hidden.Nested") && !non.Types.Single(t => t.Name == "Outer").Members.Any(m => m.Name == "Private"), "Non-private filtering failed.");
        var all = Analyze(source, Visibility.All); Check(all.Types.Any(t => t.Accessibility == "file") && all.Types.Single(t => t.Name == "Outer").Members.Any(m => m.Name == "Private"), "All filtering failed.");
    }
    private void Identities()
    {
        var doc = Analyze("public class Item { public Item? Get(int x) => null; public Item? Get(string x) => null; public System.Threading.Tasks.Task<Item?> GetAsync() => null!; }");
        var type = doc.Types.Single(); Check(type.Members.Where(m => m.Name == "Get").Select(m => m.Id).Distinct().Count() == 2, "Overloads collided.");
        var parts = type.Members.Single(m => m.Name == "GetAsync").Declaration;
        Check(parts.Any(p => p.TargetId == type.Id) && !parts.Any(p => p.TargetId is not null && p.Text == "Task"), "Internal/external links incorrect.");
        var duplicate = new SymbolAnalyzer([new("A", "A.csproj", Compile("namespace Same; public class Item {}", "A")), new("B", "B.csproj", Compile("namespace Same; public class Item {}", "B"))], root, Visibility.Public).Analyze("Input.sln");
        Check(duplicate.Types.Select(t => t.Anchor).Distinct().Count() == 2, "Cross-project IDs collided.");
        Check(type.Anchor == Analyze("public class Item {}").Types.Single().Anchor, "Type anchor changed with members.");
    }
    private async Task Documentation()
    {
        var doc = Analyze("""
            public interface IWorker {
              /// <summary>Runs with <see cref="Target"/>.</summary><param name="value">Input.</param><returns>Result.</returns>
              int Run(int value);
            }
            public class Target : IWorker {
              /// <inheritdoc/>
              public int Run(int value) => value;
              /// <summary>&lt;script&gt;alert(1)&lt;/script&gt; <see href="javascript:alert(1)">bad</see></summary>
              /// <remarks><para>A paragraph.</para><list type="bullet"><item><description>One</description></item></list></remarks>
              /// <example><code>var x = "```";</code></example>
              public void Example() {}
            }
            """);
        var type = doc.Types.Single(t => t.Name == "Target"); var member = type.Members.Single(m => m.Name == "Run");
        Check(member.Documentation.Xml.Contains("Runs with") && member.Documentation.InheritedFrom is not null, "Interface inheritdoc failed.");
        Check(member.Documentation.References.Values.Contains(type.Id), "Cref resolution failed.");
        var options = Options("xml"); await SiteRenderer.RenderAsync(doc, options, options.Output);
        var html = File.ReadAllText(Path.Combine(options.Output, "types", type.Anchor + ".html"));
        Check(!html.Contains("<script>alert(1)</script>") && !html.Contains("href=\"javascript:"), "Unsafe XML rendered.");
        using var search = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Output, "search-index.json")));
        Check(search.RootElement.EnumerateArray().Any(e => e.GetProperty("summary").GetString() == "Runs with Target."), "Search summary lost the see reference name.");
        Check(html.Contains("&lt;script&gt;") && html.Contains("<ul>") && html.Contains("<pre><code>"), "XML structure lost.");
        Check(Example().Types.Single(t => t.Name == "Repository<T>").Members.Single(m => m.Name == "Start").Documentation.Xml.Contains("Starts this service"), "Override inheritdoc failed.");
    }
    private async Task Renderers()
    {
        var first = Options("render1", "html", "markdown", "json") with { SingleFile = true }; var second = first with { Output = Options("render2").Output };
        await SiteRenderer.RenderAsync(Example(), first, first.Output); await SiteRenderer.RenderAsync(Example(), second, second.Output);
        foreach (var file in Directory.GetFiles(first.Output, "*", SearchOption.AllDirectories)) Check(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(second.Output, Path.GetRelativePath(first.Output, file)))), "Non-deterministic: " + file);
        Check(File.ReadAllText(Path.Combine(first.Output, "markdown/api.md")).Contains("](#s-"), "Single-file links missing.");
        using var searchIndex = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.Output, "html/search-index.json")));
        Check(searchIndex.RootElement.EnumerateArray().Any(e => e.GetProperty("summary").GetString() == "Stores values of T."), "Summary lost typeparamref text.");
        Check(File.Exists(Path.Combine(first.Output, "html/assets/search-index.js")) && !File.ReadAllText(Path.Combine(first.Output, "html/assets/search.js")).Contains("fetch("), "Search needs a server.");
        ValidateLinks(Path.Combine(first.Output, "html"), true);
        var multi = Options("multi", "markdown"); await SiteRenderer.RenderAsync(Example(), multi, multi.Output); ValidateLinks(multi.Output, false);
        var no = Options("no-search") with { Search = false }; await SiteRenderer.RenderAsync(Example(), no, no.Output);
        Check(!File.Exists(Path.Combine(no.Output, "search-index.json")) && !File.ReadAllText(Path.Combine(no.Output, "index.html")).Contains("search.js"), "No-search ignored.");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.Output, "json/api.json"))); Check(json.RootElement.GetProperty("types").GetArrayLength() == Example().Types.Count, "JSON incomplete.");
    }
    private void ValidateLinks(string directory, bool html)
    {
        foreach (var file in Directory.GetFiles(directory, html ? "*.html" : "*.md", SearchOption.AllDirectories))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), html ? "href=\"([^\"]+)\"" : @"\]\(([^)]+)\)"))
            {
                var url = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value); if (url.Contains("://") || url.StartsWith("mailto:")) continue;
                var split = url.Split('#', 2); var target = split[0].Length == 0 ? file : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, split[0]));
                Check(File.Exists(target), "Broken link " + url);
                if (split.Length == 2) Check(File.ReadAllText(target).Contains("id=\"" + split[1] + "\""), "Broken anchor " + url);
            }
    }
    private async Task Transactions()
    {
        var options = Options("transaction");
        using (var tx = new OutputTransaction(options)) { await tx.RenderAsync(Example(), options); tx.Commit(); }
        var entry = Path.Combine(options.Output, "index.html"); var before = File.ReadAllText(entry); var stale = Path.Combine(options.Output, "stale.html"); File.WriteAllText(stale, "stale");
        using (var tx = new OutputTransaction(options)) { await tx.RenderAsync(Analyze("public class Changed {}"), options); }
        Check(File.ReadAllText(entry) == before && File.Exists(stale), "Aborted transaction changed output.");
        using (var tx = new OutputTransaction(options)) { await tx.RenderAsync(Analyze("public class Changed {}"), options); tx.Commit(); }
        Check(!File.Exists(stale), "Snapshot retained deleted files.");
        try { using var tx = new OutputTransaction(options with { Output = root, Clean = true }); throw new Exception("Source accepted."); } catch (ArgumentExceptionWithUsage) { assertions++; }
        var unmanaged = Options("unmanaged").Output; Directory.CreateDirectory(unmanaged); File.WriteAllText(Path.Combine(unmanaged, "keep.txt"), "keep");
        try { using var tx = new OutputTransaction(options with { Output = unmanaged }); throw new Exception("Unmanaged accepted."); } catch (ArgumentExceptionWithUsage) { assertions++; }
        Check(File.Exists(Path.Combine(unmanaged, "keep.txt")), "Unmanaged files lost.");
        using (var tx = new OutputTransaction(options)) { try { using var concurrent = new OutputTransaction(options); throw new Exception("Concurrent writer accepted."); } catch (IOException) { assertions++; } }
        if (!OperatingSystem.IsWindows())
        {
            var symlink = Options("symlink").Output; Directory.CreateSymbolicLink(symlink, options.Output);
            try { using var tx = new OutputTransaction(options with { Output = symlink }); throw new Exception("Symlink accepted."); } catch (ArgumentExceptionWithUsage) { assertions++; }
            Directory.Delete(symlink);
        }
    }
    private void ConfigurationRules()
    {
        var directory = Options("config").Output; Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "One.csproj"), "<Project/>");
        Check(Configuration.Parse(["build"], directory).Input.EndsWith("One.csproj"), "Discovery failed.");
        File.WriteAllText(Path.Combine(directory, "Two.sln"), ""); Invalid(["build"], directory); File.Delete(Path.Combine(directory, "Two.sln"));
        File.WriteAllText(Path.Combine(directory, "codedocs.json"), """{"formats":["json"],"visibility":"all","analysis":{"onIncomplete":"overwrite"},"html":{"search":false},"source":{"showDeclarationFiles":false}}""");
        var config = Configuration.Parse(["build", "--format", "markdown", "--visibility", "api", "--single-file"], directory);
        Check(config.Formats.SequenceEqual(new[] { "markdown" }) && config.Visibility == Visibility.Api && config.SingleFile && config.OnIncomplete == IncompletePolicy.Overwrite && !config.Search && !config.ShowDeclarationFiles, "Precedence/config failed.");
        foreach (string[] invalid in new string[][] { ["build", "--format", "html", "--single-file"], ["build", "--format", "pdf"], ["build", "--visibility", "bogus"], ["build", "--output"], ["build", "--unknown"] }) Invalid(invalid, directory);
        File.WriteAllText(Path.Combine(directory, "codedocs.json"), "{\"unexpected\":true}"); Invalid(["build"], directory);
        var child = Path.Combine(directory, "child"); Directory.CreateDirectory(child); Invalid(["build"], child);
    }
    private void Invalid(string[] args, string directory) { try { Configuration.Parse(args, directory); throw new Exception("Invalid configuration accepted."); } catch (ArgumentExceptionWithUsage) { assertions++; } }
    private async Task Integration()
    {
        var directory = Options("integration").Output; Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "Api.csproj"); var source = Path.Combine(directory, "Api.cs"); var destination = Path.Combine(directory, "output");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        File.WriteAllText(source, "public class Api { public string? Value { get; init; } }"); await Dotnet(directory, "restore", project, "-m:1");
        var stdout = new StringWriter(); var stderr = new StringWriter();
        async Task<int> Run(params string[] extra) => await Cli.RunAsync(["build", project, "-o", destination, .. extra], directory, stdout, stderr);
        Check(await Run() == 0, stderr.ToString()); var before = File.ReadAllText(Path.Combine(destination, "index.html"));
        File.WriteAllText(source, "public class Api { public Missing Value { get; set; } }");
        Check(await Run() == 2 && File.ReadAllText(Path.Combine(destination, "index.html")) == before, "Keep-existing failed.");
        Check(await Run("--on-incomplete", "fail") == 2 && File.ReadAllText(Path.Combine(destination, "index.html")) == before, "Fail policy failed.");
        Check(await Run("--on-incomplete", "overwrite") == 2 && File.ReadAllText(Path.Combine(destination, "index.html")).Contains("incomplete compilation"), "Partial HTML failed.");
        Check(await Run("--on-incomplete", "overwrite", "--format", "markdown", "--single-file") == 2 && File.ReadAllText(Path.Combine(destination, "api.md")).Contains("**Warning:**"), "Partial Markdown failed.");
        File.WriteAllText(source, "public class Api {}"); await Dotnet(directory, "new", "sln", "-n", "Api", "--format", "sln"); await Dotnet(directory, "sln", "Api.sln", "add", "Api.csproj");
        Check(await Cli.RunAsync(["build", Path.Combine(directory, "Api.sln"), "-o", destination], directory, stdout, stderr) == 0, "Solution failed: " + stderr);
        var final = File.ReadAllText(Path.Combine(destination, "index.html")); File.WriteAllText(project, "<Project><Broken></Project>");
        Check(await Run() == 1 && File.ReadAllText(Path.Combine(destination, "index.html")) == final, "Fatal load changed output.");
        Check(await Cli.RunAsync(["build", "--unknown"], directory, stdout, stderr) == 3, "Invalid exit code failed.");
    }
    private static async Task Dotnet(string directory, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await stdout + await stderr);
    }
    private static string FindRoot() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "CodeDocs.CSharp.sln"))) d = d.Parent; return d?.FullName ?? throw new Exception("Root not found."); }
}

// Invoked directly by the test driver; this fixture is not a distributed compiler extension.
#pragma warning disable RS1036, RS1038, RS1041
internal sealed class GeneratedFixture : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) => context.RegisterPostInitializationOutput(output =>
        output.AddSource("Generated.g.cs", "// <auto-generated/>\npublic partial class Generated { public void FromGenerator() {} }"));
}
#pragma warning restore RS1036, RS1038, RS1041
