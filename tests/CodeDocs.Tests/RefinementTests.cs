using System.Text.Json;
using System.Xml.Linq;
using CodeDocs.Model;
using CodeDocs.Rendering;

internal static class RefinementTests
{
    public static void Semantics(Func<string, SolutionDocument> analyze, Action<bool, string> check)
    {
        var doc = analyze(SemanticSource);
        check(doc.State == AnalysisState.Success, string.Join("\n", doc.Diagnostics.Where(d => d.Severity == "Error")));
        var entry = doc.Types.Single(t => t.Name == "Entry");
        var attribute = entry.Attributes.Single(a => a.Type?.Name == "TagAttribute");
        check(attribute.ConstructorId is not null, "Attribute constructor identity missing.");
        check(attribute.ConstructorValues[0].Kind == "Type" && attribute.ConstructorValues[0].ReferencedType?.TargetId == entry.Id, "typeof target lost.");
        check(attribute.ConstructorValues[1].Kind == "Enum" && attribute.ConstructorValues[1].Type?.Name == "Mode" && Convert.ToInt64(attribute.ConstructorValues[1].Value) == 3, "Enum constant lost its type/value.");
        check(attribute.ConstructorValues[2].Elements.Select(e => Convert.ToInt32(e.Value)).SequenceEqual([1, 2]), "Attribute array lost its values.");
        check(attribute.NamedValues["Note"].IsNull && attribute.NamedValues["Alias"].ReferencedType?.MetadataName == "String", "Named/null/typeof attribute values lost.");
        check(entry.Relationships.Any(r => r.Kind == "AttributeArgument" && r.Type.TargetId == entry.Id), "Attribute argument relationship missing.");
        var values = entry.Members.Single(m => m.Name == "Values").ValueType!;
        check(values.ArrayRank == 1 && values.IsSZArray == true && values.NullableAnnotation == "Annotated", "Array metadata lost.");
        var tuple = values.ElementType!;
        check(tuple.TupleElements.Select(e => e.Name).SequenceEqual(["label", "result"]) && tuple.TupleElements.All(e => e.IsExplicitlyNamed), "Named tuple metadata missing.");
        check(tuple.TupleElements[0].Type.NullableAnnotation == "Annotated" && tuple.TupleElements[1].Type.TupleElements.Count == 2, "Nested/nullable tuple metadata lost.");
        var grid = entry.Members.Single(m => m.Name == "Grid").ValueType!;
        check(grid.ArrayRank == 3 && grid.IsSZArray == false && grid.ElementType?.MetadataName == "Int32", "Multidimensional array metadata lost.");
        var pointer = entry.Members.Single(m => m.Name == "Pointer").ValueType!.FunctionPointer!;
        check(pointer.Parameters.Select(p => p.RefKind).SequenceEqual(["Ref", "Out"]) && pointer.ReturnType.MetadataName == "Int32", "Function pointer signature lost.");
        check(pointer.UnmanagedCallingConventionTypes.Any(t => t.Name == "CallConvCdecl") || pointer.CallingConvention == "CDecl", "Function pointer calling convention lost.");
        check(entry.Members.Single(m => m.Name == "Raw").ValueType?.PointedAtType?.MetadataName == "Byte", "Pointer element type lost.");
        check(entry.Members.Single(m => m.Name == "Native").ValueType?.IsNativeInteger == true, "Native integer flag lost.");
        var optional = entry.Members.Single(m => m.Name == "Optional");
        check(optional.Parameters[0].Default?.Kind == "Enum" && optional.Parameters[1].Default?.IsNull == true, "Typed parameter defaults missing.");
        var create = entry.Members.Single(m => m.Name == "Create");
        check(create.ValueType?.TypeParameter?.Ordinal == 0 && create.ValueType.TypeParameter.Kind == "Method", "Type parameter identity missing.");
        check(create.GenericParameters.Single().ConstraintDetails.Select(c => c.Kind).SequenceEqual(["ReferenceType", "Type", "Constructor"]), "Structured constraints lost.");
        check(create.GenericParameters.Single().ConstraintDetails[1].Type?.TargetId is not null, "Constraint reference missing.");
        var setter = entry.Members.Single(m => m.Name == "Value").AccessorDetails.Single(a => a.Kind == "PropertySet");
        check(setter.Parameters.Single().Attributes.Any(a => a.Type?.Name == "TagAttribute"), "Setter value attributes lost.");
        var factory = doc.Types.Single(t => t.Name == "Factory").DelegateSignature!;
        check(factory.Attributes.Any(a => a.Target == "return") && factory.Parameters.Single().Attributes.Count > 0, "Delegate attribute targets missing.");
        check(doc.Projects.Single().Attributes.Any(a => a.Target == "module" && a.Type?.Name == "TagAttribute"), "Module attributes missing.");
        var nested = analyze("public class Deep { public int" + string.Concat(Enumerable.Repeat("[]", 12)) + "? Values; }");
        check(JsonSerializer.Serialize(nested, SiteRenderer.JsonOptions).Length < 100_000, "Nested array types were duplicated exponentially in JSON.");
        var json = JsonSerializer.Serialize(doc, SiteRenderer.JsonOptions);
        using var parsed = JsonDocument.Parse(json);
        check(json.Contains("NaN", StringComparison.Ordinal) && parsed.RootElement.GetProperty("types").GetArrayLength() == doc.Types.Count, "Special numeric constant prevented JSON output.");
    }

    public static async Task Documentation(Func<string, SolutionDocument> analyze, Action<bool, string> check, string scratch)
    {
        var doc = analyze(XmlSource);
        check(doc.State == AnalysisState.Success, string.Join("\n", doc.Diagnostics.Where(d => d.Severity == "Error")));
        var worker = doc.Types.Single(t => t.Name == "Worker");
        var run = worker.Members.Single(m => m.Name == "Run").Documentation;
        var xml = XElement.Parse(run.Xml);
        check(xml.Element("param")?.Attribute("name")?.Value == "current" && xml.Element("typeparam")?.Attribute("name")?.Value == "TResult", "Inherited parameter/type-parameter names were not remapped.");
        check(xml.Descendants("paramref").Any(e => e.Attribute("name")?.Value == "current") && xml.Descendants("typeparamref").Any(e => e.Attribute("name")?.Value == "TResult"), "Inline inherited references were not remapped.");
        check(run.RawXml.Contains("inheritdoc", StringComparison.Ordinal) && run.Sections.Any(n => n.Name == "summary") && run.Inheritances.Any(i => i.Status == "Resolved"), "Raw/structured documentation or provenance lost.");
        check(Descendants(run.Sections).Any(n => n.Name == "see" && n.ReferenceId == worker.Id), "Structured cref reference missing.");
        var cases = doc.Types.Single(t => t.Name == "Cases");
        var selected = XElement.Parse(cases.Members.Single(m => m.Name == "Select").Documentation.Xml);
        check(selected.Element("summary")?.Value.Contains("Input for", StringComparison.Ordinal) == true && selected.Descendants("paramref").Single().Attribute("name")?.Value == "current", "XPath content selection/remapping failed.");
        check(XElement.Parse(cases.Members.Single(m => m.Name == "Absolute").Documentation.Xml).Element("summary")?.Value.Contains("Base operation", StringComparison.Ordinal) == true, "Absolute /member XPath failed.");
        var own = XElement.Parse(cases.Members.Single(m => m.Name == "Own").Documentation.Xml);
        check(own.Element("summary")?.Value == "Local summary." && own.Element("param")?.Attribute("name")?.Value == "current", "Local documentation precedence failed.");
        var child = doc.Types.Single(t => t.Name == "Child");
        check(!child.InheritedMembers.Any(m => m.Name.Contains("Echo", StringComparison.Ordinal)), "Renamed generic override was duplicated as an inherited member.");
        check(cases.Members.Single(m => m.Name == "Scalar").Documentation.Inheritances.Any(i => i.Status == "InvalidPath"), "Scalar XPath result was treated as XML content.");
        check(child.Members.Single(m => m.Name == "Execute").Documentation.Xml.Contains("Base operation", StringComparison.Ordinal), "Undocumented override interrupted inheritance lookup.");
        check(doc.Diagnostics.Any(d => d.Code == "INHERITDOC_PATH") && doc.Diagnostics.Any(d => d.Code == "INHERITDOC_EMPTY") && doc.Diagnostics.Any(d => d.Code == "INHERITDOC_CYCLE"), "Invalid/empty/cyclic inheritdoc did not produce warnings.");
        check(cases.Members.Single(m => m.Name == "BadPath").Documentation.Xml.Contains("inheritdoc", StringComparison.Ordinal), "Invalid inheritdoc was discarded.");
        var output = Path.Combine(scratch, "refined-xml");
        var options = new BuildOptions { Input = "fixture.csproj", Output = output, Formats = ["html", "markdown", "json"], SingleFile = true };
        await SiteRenderer.RenderAsync(doc, options, output);
        var html = File.ReadAllText(Path.Combine(output, "html/types", cases.Anchor + ".html"));
        var markdown = File.ReadAllText(Path.Combine(output, "markdown/api.md"));
        check(html.Contains("<table class=\"doc-table\">", StringComparison.Ordinal) && html.Contains("<th>Key</th>", StringComparison.Ordinal), "HTML XML table missing.");
        check(markdown.Contains("| Key | Meaning |", StringComparison.Ordinal) && markdown.Contains("| --- | --- |", StringComparison.Ordinal), "Markdown XML table missing.");
        check(html.Contains("System.InvalidOperationException</a>", StringComparison.Ordinal) || html.Contains("System.InvalidOperationException — Failure description", StringComparison.Ordinal), "Exception type was replaced by its description.");
        var semantic = analyze(SemanticSource);
        var attributesOutput = Path.Combine(scratch, "refined-attributes");
        await SiteRenderer.RenderAsync(semantic, options with { Output = attributesOutput }, attributesOutput);
        var factory = semantic.Types.Single(t => t.Name == "Factory");
        var factoryHtml = File.ReadAllText(Path.Combine(attributesOutput, "html/types", factory.Anchor + ".html"));
        check(factoryHtml.Contains("Attributes on parameter value", StringComparison.Ordinal) && factoryHtml.Contains("return: ", StringComparison.Ordinal), "Delegate attributes not rendered.");
        var entry = semantic.Types.Single(t => t.Name == "Entry");
        var entryHtml = File.ReadAllText(Path.Combine(attributesOutput, "html/types", entry.Anchor + ".html"));
        check(entryHtml.Contains("Attributes on PropertySet parameter value", StringComparison.Ordinal), "Accessor parameter attributes not rendered.");
        check(File.ReadAllText(Path.Combine(attributesOutput, "markdown/api.md")).Contains("module: [TagAttribute", StringComparison.Ordinal), "Module attributes not rendered in Markdown.");
    }
    private static IEnumerable<DocumentationNode> Descendants(IEnumerable<DocumentationNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Descendants(n.Children)));

    internal const string SemanticSource = """
        using System;
        [assembly: Tag(typeof(Entry), Mode.One, new int[] { 1 })]
        [module: Tag(typeof(Entry), Mode.One, null)]
        public enum Mode : ushort { One = 1, Three = 3 }
        [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
        public sealed class TagAttribute(Type type, Mode mode, int[]? numbers) : Attribute
        {
            public string? Note { get; set; }
            public Type? Alias { get; set; }
        }
        public interface IEntity { }
        [Tag(typeof(Entry), Mode.Three, new int[] { 1, 2 }, Note = null, Alias = typeof(string))]
        public unsafe class Entry
        {
            public (string? label, (int code, bool ok) result)[]? Values;
            public int[,,]? Grid;
            public delegate* unmanaged[Cdecl]<ref int, out byte, int> Pointer;
            public byte* Raw;
            public nint Native;
            public const double Missing = double.NaN;
            public void Optional(Mode mode = Mode.Three, string? text = null) { }
            public T Create<T>() where T : class, IEntity, new() => new();
            public int Value { [return: Tag(typeof(Entry), Mode.One, null)] get; [param: Tag(typeof(Entry), Mode.One, null)] set; }
        }
        [return: Tag(typeof(Entry), Mode.One, null)]
        public delegate Entry? Factory([Tag(typeof(Entry), Mode.One, null)] Entry value);
        """;

    internal const string XmlSource = """
        using System;
        public interface IWorker
        {
            /// <summary>Runs <typeparamref name="TValue"/> with <paramref name="oldName"/> and <see cref="Worker"/>.</summary>
            /// <param name="oldName">Input.</param><typeparam name="TValue">Output type.</typeparam><returns>Result.</returns>
            TValue Run<TValue>(int oldName);
        }
        public class Worker : IWorker
        {
            /// <inheritdoc/>
            public TResult Run<TResult>(int current) => default!;
        }
        public class Base
        {
            /// <summary>Base operation.</summary><param name="old">Input for <paramref name="old"/>.</param><returns>Result.</returns>
            public virtual int Execute(int old) => old;
            /// <summary>Echoes a value.</summary>
            public virtual T Echo<T>(T old) => old;
        }
        public class Middle : Base { public override int Execute(int middle) => middle; }
        public class Child : Middle
        {
            /// <inheritdoc/>
            public override int Execute(int current) => current;
            /// <inheritdoc/>
            public override TResult Echo<TResult>(TResult current) => current;
        }
        public class Cases
        {
            /// <summary><inheritdoc cref="Base.Execute" path="/param[@name='old']/node()"/></summary>
            public void Select(int current) { }
            /// <summary><inheritdoc cref="Base.Execute" path="/member/summary/node()"/></summary>
            public void Absolute() { }
            /// <summary>Local summary.</summary><inheritdoc cref="Base.Execute"/>
            public void Own(int current) { }
            /// <inheritdoc cref="Base.Execute" path="/summary["/>
            public void BadPath() { }
            /// <inheritdoc cref="Base.Execute" path="/param[@name='missing']"/>
            public void Empty() { }
            /// <inheritdoc cref="Base.Execute" path="count(param)"/>
            public void Scalar() { }
            /// <inheritdoc cref="CycleB"/>
            public void CycleA() { }
            /// <inheritdoc cref="CycleA"/>
            public void CycleB() { }
            /// <summary>A table.</summary>
            /// <remarks><list type="table"><listheader><term>Key</term><description>Meaning</description></listheader><item><term><see cref="Worker"/></term><description>Known <c>value</c>.</description></item></list></remarks>
            /// <exception cref="InvalidOperationException">Failure description.</exception>
            public void Table() { }
        }
        """;
}
