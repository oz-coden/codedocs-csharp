using System.Text.Json;
using CodeDocs.Model;
using CodeDocs.Rendering;

internal static class MaintenanceTests
{
    public static async Task Attributes(Func<string, Visibility, SolutionDocument> analyze, Action<bool, string> check, string scratch)
    {
        foreach (var visibility in Enum.GetValues<Visibility>())
        {
            var doc = analyze(AttributeSource, visibility);
            check(doc.State == AnalysisState.Success, string.Join("\n", doc.Diagnostics.Where(d => d.Severity == "Error")));
            var type = doc.Types.Single(t => t.Name == "Entry");
            foreach (var name in new[] { "Name", "Changed", "Other" })
            {
                var member = type.Members.Single(m => m.Name == name);
                var field = member.Attributes.Single(a => a.Target == "field" && a.Type?.Name == "MarkAttribute");
                check(field.Type?.TargetId is not null && field.ConstructorId is not null, "Field attribute symbol links lost: " + name);
                check(field.ConstructorValues[0].ReferencedType?.TargetId == type.Id, "Field typeof argument lost: " + name);
                check(field.ConstructorValues[1].Kind == "Enum" && Convert.ToInt32(field.ConstructorValues[1].Value) == 3, "Field enum argument lost: " + name);
                check(field.ConstructorValues[2].Elements.Select(e => Convert.ToInt32(e.Value)).SequenceEqual([1, 2]), "Field array argument lost: " + name);
                check(field.NamedValues["Note"].Value as string == "field" && field.NamedValues["Alias"].ReferencedType?.TargetId == type.Id, "Field named arguments lost: " + name);
                check(field.NamedValues["Empty"].IsNull && field.NamedValues["Empty"].Kind == "Array", "Null field array lost: " + name);
                check(member.Relationships.Any(r => r.Kind == "AttributeArgument" && r.Type.TargetId == type.Id), "Field attribute relationships missing: " + name);
            }
            check(type.Members.All(m => !m.Name.Contains("k__BackingField", StringComparison.Ordinal)), "Backing field exposed as a normal API.");
            var property = type.Members.Single(m => m.Name == "Name");
            check(property.Attributes.Any(a => a.Target == "field" && a.Type?.Name == "NonSerializedAttribute"), "NonSerialized field target missing.");
            check(property.Attributes.Any(a => a.Target == "symbol" && a.Type?.Name == "MarkAttribute"), "Property attribute was replaced by its field attributes.");
            var changed = type.Members.Single(m => m.Name == "Changed");
            check(changed.AccessorDetails.All(a => a.Attributes.Any(x => x.Type?.Name == "MarkAttribute")), "Event method-target attributes lost.");
            var positional = doc.Types.Single(t => t.Name == "Positional");
            check(positional.Members.Single(m => m.Name == "Value").Attributes.Any(a => a.Target == "field"), "Record positional field attribute lost.");
            check(positional.Members.Single(m => m.IsPrimaryConstructor).Parameters.Single().Attributes.Count > 0, "Record parameter attribute lost.");
            if (visibility != Visibility.Public) continue;
            var output = Path.Combine(scratch, "field-attributes");
            await SiteRenderer.RenderAsync(doc, new BuildOptions { Input = "fixture.csproj", Output = scratch, Formats = ["html", "markdown", "json"], SingleFile = true }, output);
            check(File.ReadAllText(Path.Combine(output, "html/types", type.Anchor + ".html")).Contains("field: ", StringComparison.Ordinal), "Field target not rendered in HTML.");
            check(File.ReadAllText(Path.Combine(output, "markdown/api.md")).Contains("field: ", StringComparison.Ordinal), "Field target not rendered in Markdown.");
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "json/api.json")));
            check(json.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Entry")
                .GetProperty("members").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Changed")
                .GetProperty("attributes").EnumerateArray().Any(a => a.GetProperty("target").GetString() == "field"), "Field target not serialized.");
        }
        var withoutFields = analyze("using System; public abstract class NoFields { [field: NonSerialized] public abstract event Action Changed; [field: NonSerialized] public extern event Action External; }", Visibility.Public);
        check(withoutFields.State == AnalysisState.Success && withoutFields.Types.Single().Members.Where(m => m.Category == "Events").All(m => m.Attributes.All(a => a.Target != "field")), "Ignored field-target attributes created fictitious backing fields.");
        var partial = analyze("using System; public class Entry { [field: Missing(typeof(Entry))] public event Action? Changed; }", Visibility.Public);
        check(partial.State == AnalysisState.Partial && partial.Types.Single().Members.Single(m => m.Name == "Changed").Attributes.Any(a => a.Target == "field" && a.Display.Contains("Missing", StringComparison.Ordinal)), "Unbound field attribute was discarded.");
    }

    public static async Task Renderers(SolutionDocument doc, Action<bool, string> check, string scratch)
    {
        // Each renderer must be usable directly from IR, independently of the format router.
        var combined = Path.Combine(scratch, "combined-renderers");
        var direct = Path.Combine(scratch, "direct-renderers");
        var options = new BuildOptions { Input = "fixture.csproj", Output = scratch, Formats = ["html", "markdown", "json"], SingleFile = true };
        await SiteRenderer.RenderAsync(doc, options, combined);
        HtmlRenderer.Render(doc, options, Path.Combine(direct, "html"));
        MarkdownRenderer.Render(doc, options, Path.Combine(direct, "markdown"));
        JsonRenderer.Render(doc, Path.Combine(direct, "json"));
        var files = Directory.GetFiles(combined, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(combined, f)).Order(StringComparer.Ordinal).ToArray();
        check(files.SequenceEqual(Directory.GetFiles(direct, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(direct, f)).Order(StringComparer.Ordinal)), "Router and direct renderer file sets differ.");
        foreach (var file in files) check(File.ReadAllBytes(Path.Combine(combined, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(direct, file))), "Router changed renderer content: " + file);
        var malformed = doc with { Types = doc.Types.Select((t, i) => i == 0 ? t with { Documentation = new("<member><summary>", "<member><summary>", new Dictionary<string, string>()) } : t).ToArray() };
        await SiteRenderer.RenderAsync(malformed, options, Path.Combine(scratch, "malformed-ir"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledOutput = Path.Combine(scratch, "cancelled-render");
        var cancelledRender = SiteRenderer.RenderAsync(doc, options, cancelledOutput, cancelled.Token);
        check(cancelledRender.IsCanceled, "Async renderer did not return a cancelled task.");
        try { await cancelledRender; throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { check(!Directory.Exists(cancelledOutput), "Cancelled rendering wrote files."); }
        try { await SiteRenderer.RenderAsync(doc, options with { Formats = ["unsupported"] }, Path.Combine(scratch, "invalid-format")); throw new Exception("Unknown format silently ignored."); }
        catch (ArgumentException) { check(true, "Unsupported format rejected."); }
    }

    public static async Task Xml(Func<string, SolutionDocument> analyze, Action<bool, string> check, string scratch)
    {
        var doc = analyze("""
            public class XmlCases {
                /// <summary>Known <custom label="kept">custom <c>content</c></custom> and `ticks` &amp;copy;.</summary>
                /// <remarks><list type="bullet"><item>Plain item.</item><item>Use <see cref="XmlCases"/> here.</item></list></remarks>
                /// <vendor data="kept"><nested>Raw extension.</nested></vendor>
                /// <seealso cref="DoesNotExist"/>
                public void Unknown() { }
                /// <summary>Broken &unknown; entity.</summary>
                public void Malformed() { }
                /// <inheritdoc cref="DoesNotExist"/>
                public void Missing() { }
            }
            """);
        var type = doc.Types.Single();
        var unknown = type.Members.Single(m => m.Name == "Unknown").Documentation;
        check(unknown.RawXml.Contains("vendor", StringComparison.Ordinal) && unknown.Sections.Any(s => s.Name == "vendor"), "Unknown XML tags were removed from IR.");
        check(type.Members.Single(m => m.Name == "Malformed").Documentation.RawXml.Length > 0, "Malformed raw XML was lost.");
        check(doc.Diagnostics.Any(d => d.Severity == "Warning") && type.Members.Single(m => m.Name == "Missing").Documentation.Xml.Contains("inheritdoc", StringComparison.Ordinal), "Unresolved XML inheritance was lost.");
        var output = Path.Combine(scratch, "unknown-xml");
        await SiteRenderer.RenderAsync(doc, new BuildOptions { Input = "fixture.csproj", Output = scratch, Formats = ["html", "markdown", "json"], SingleFile = true }, output);
        var html = File.ReadAllText(Path.Combine(output, "html/types", type.Anchor + ".html"));
        var md = File.ReadAllText(Path.Combine(output, "markdown/api.md"));
        check(html.Contains("custom <code>content</code>", StringComparison.Ordinal), "Unknown inline XML contents were lost.");
        check(html.Contains("<li>Plain item.</li>", StringComparison.Ordinal) && md.Contains("- Plain item.", StringComparison.Ordinal), "Plain XML list item was lost.");
        check(html.Contains("XmlCases</a> here.</li>", StringComparison.Ordinal) && md.Contains(" here.", StringComparison.Ordinal), "Mixed inline list item content was lost.");
        check(md.Contains(@"\`ticks\` &amp;copy;", StringComparison.Ordinal), "Markdown text escaping lost literal code/entity text.");
    }

    private const string AttributeSource = """
        using System;
        public enum Mode { Three = 3 }
        [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
        public sealed class MarkAttribute(Type type, Mode mode, params int[] values) : Attribute {
            public string? Note { get; set; }
            public Type? Alias { get; set; }
            public int[]? Empty { get; set; }
        }
        public class Entry {
            [property: Mark(typeof(Entry), Mode.Three, 1, 2)]
            [field: NonSerialized, Mark(typeof(Entry), Mode.Three, 1, 2, Note = "field", Alias = typeof(Entry), Empty = null)]
            public string? Name { get; set; }
            [field: Mark(typeof(Entry), Mode.Three, new int[] { 1, 2 }, Note = "field", Alias = typeof(Entry), Empty = null)]
            [method: Mark(typeof(Entry), Mode.Three)]
            public event Action? Changed, Other;
        }
        public record Positional([field: Mark(typeof(Entry), Mode.Three)] [param: Mark(typeof(Entry), Mode.Three)] string Value);
        """;
}
