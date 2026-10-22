using System.Collections;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using CodeDocs.Model;
using Microsoft.CodeAnalysis;

namespace CodeDocs.Analysis;

internal sealed class XmlDocumentationReader(Compilation compilation, Func<ISymbol?, string?> resolve, ICollection<DiagnosticDocument> diagnostics)
{
    public DocumentationComment Read(ISymbol symbol)
    {
        var raw = symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: false) ?? "";
        if (raw.Length == 0) return new("", "", new SortedDictionary<string, string>(StringComparer.Ordinal));
        var inheritances = new List<DocumentationInheritance>();
        try
        {
            var xml = Parse(symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: true) ?? raw);
            Expand(symbol, xml, new HashSet<ISymbol>(SymbolEqualityComparer.Default), inheritances);
            var references = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var cref in xml.DescendantsAndSelf().Attributes("cref").Select(a => a.Value).Distinct())
                if (resolve(Reference(cref)) is { } id) references[cref] = id;
            return new(raw, xml.ToString(SaveOptions.DisableFormatting), references,
                inheritances.FirstOrDefault(i => i.Status == "Resolved")?.TargetId ?? inheritances.FirstOrDefault(i => i.Status == "Resolved")?.DocumentationId)
            {
                Sections = xml.Nodes().Select(n => Node(n, references)).ToArray(),
                Inheritances = inheritances
            };
        }
        catch (XmlException e)
        {
            Warn("XMLDOC", symbol, e.Message);
            return new(raw, "", new SortedDictionary<string, string>(StringComparer.Ordinal)) { Inheritances = inheritances };
        }
    }

    private ISymbol? Reference(string cref) => DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation)
        ?? DocumentationCommentId.GetFirstSymbolForReferenceId(cref, compilation);
    private void Warn(string code, ISymbol symbol, string message) => diagnostics.Add(new("Warning", code, symbol.ToDisplayString() + ": " + message));
    private static XElement Parse(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static DocumentationNode Node(XNode node, IReadOnlyDictionary<string, string> references)
    {
        if (node is XElement element)
        {
            var attributes = new SortedDictionary<string, string>(element.Attributes().ToDictionary(a => a.Name.ToString(), a => a.Value), StringComparer.Ordinal);
            var target = attributes.TryGetValue("cref", out var cref) && references.TryGetValue(cref, out var id) ? id : null;
            return new("Element", element.Name.ToString(), null, attributes, element.Nodes().Select(n => Node(n, references)).ToArray(), target);
        }
        return new(node is XCData ? "CData" : node is XText ? "Text" : node is XComment ? "Comment" : node.NodeType.ToString(), null,
            node is XText text ? text.Value : node is XComment comment ? comment.Value : node.ToString(), new SortedDictionary<string, string>(StringComparer.Ordinal), []);
    }

    private void Expand(ISymbol symbol, XElement xml, HashSet<ISymbol> visited, List<DocumentationInheritance> inheritances)
    {
        if (!visited.Add(symbol)) return;
        try
        {
            foreach (var inherit in xml.Descendants("inheritdoc").ToArray())
            {
                var path = inherit.Attribute("path")?.Value;
                var cref = inherit.Attribute("cref")?.Value;
                var candidates = cref is null ? Targets(symbol) : Reference(cref) is { } target ? [target] : Array.Empty<ISymbol>();
                (ISymbol Symbol, XElement Xml)? source = null;
                foreach (var candidate in candidates)
                {
                    try { source = Documented(candidate, visited, inheritances); }
                    catch (XmlException e) { Warn("XMLDOC", candidate, e.Message); }
                    if (source is not null) break;
                }
                if (source is null)
                {
                    Warn("INHERITDOC_UNRESOLVED", symbol, "Could not resolve inheritdoc" + (cref is null ? "." : " " + cref + "."));
                    inheritances.Add(new(null, cref, path, "Unresolved"));
                    continue;
                }
                var (sourceSymbol, sourceXml) = source.Value;
                try
                {
                    var mapped = new XElement(sourceXml);
                    Remap(mapped, sourceSymbol, symbol);
                    IReadOnlyList<XNode> selected;
                    if (path is not null) selected = Select(sourceXml, path).Select(Clone).ToArray();
                    else if (inherit.Parent == xml) selected = mapped.Elements().Where(e => e.Name.LocalName != "inheritdoc").Select(e => (XNode)new XElement(e)).ToArray();
                    else
                    {
                        var parent = inherit.Parent!;
                        var name = parent.Attribute("name")?.Value ?? parent.Attribute("cref")?.Value;
                        var matching = mapped.Elements(parent.Name).FirstOrDefault(e => (e.Attribute("name")?.Value ?? e.Attribute("cref")?.Value) == name);
                        selected = matching?.Nodes().Select(Clone).ToArray() ?? [];
                    }
                    if (selected.Count == 0)
                    {
                        Warn("INHERITDOC_EMPTY", symbol, "Inheritdoc selected no XML nodes.");
                        inheritances.Add(new(resolve(sourceSymbol), sourceSymbol.GetDocumentationCommentId(), path, "Empty"));
                        continue;
                    }
                    if (path is not null)
                    {
                        foreach (var element in selected.OfType<XElement>()) Remap(element, sourceSymbol, symbol);
                    }
                    if (inherit.Parent == xml)
                    {
                        foreach (var node in selected)
                        {
                            if (node is XElement element)
                            {
                                var key = element.Attribute("name")?.Value ?? element.Attribute("cref")?.Value;
                                if (xml.Elements(element.Name).Any(e => (e.Attribute("name")?.Value ?? e.Attribute("cref")?.Value) == key)) continue;
                            }
                            inherit.AddBeforeSelf(node);
                        }
                        inherit.Remove();
                    }
                    else
                        inherit.ReplaceWith(selected);
                    inheritances.Add(new(resolve(sourceSymbol), sourceSymbol.GetDocumentationCommentId(), path, "Resolved"));
                }
                catch (XPathException e)
                {
                    Warn("INHERITDOC_PATH", symbol, e.Message);
                    inheritances.Add(new(resolve(sourceSymbol), sourceSymbol.GetDocumentationCommentId(), path, "InvalidPath"));
                }
            }
        }
        finally { visited.Remove(symbol); }
    }

    private (ISymbol Symbol, XElement Xml)? Documented(ISymbol symbol, HashSet<ISymbol> visited, List<DocumentationInheritance> inheritances)
    {
        if (visited.Contains(symbol))
        {
            Warn("INHERITDOC_CYCLE", symbol, "Cyclic documentation inheritance was preserved without expansion.");
            return null;
        }
        var raw = symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: true);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var xml = Parse(raw);
            Expand(symbol, xml, visited, inheritances);
            return (symbol, xml);
        }
        visited.Add(symbol);
        try
        {
            foreach (var target in Targets(symbol))
                if (Documented(target, visited, inheritances) is { } result) return result;
            return null;
        }
        finally { visited.Remove(symbol); }
    }

    private static IReadOnlyList<XNode> Select(XElement xml, string path)
    {
        // Documentation XPath addresses the contents of <member>; explicit /member paths also work.
        var document = new XDocument(new XElement(xml));
        var expression = path.StartsWith("/member", StringComparison.Ordinal) ? path : path.StartsWith('/') ? "." + path : path;
        var value = document.Root!.XPathEvaluate(expression);
        if (value is string || value is not IEnumerable values) throw new XPathException("Inheritdoc XPath must select XML nodes.");
        return values.Cast<object>().Select(item => item switch
        {
            XNode node => node,
            XAttribute attribute => new XText(attribute.Value),
            _ => throw new XPathException("Inheritdoc XPath returned an unsupported value.")
        }).ToArray();
    }

    private static void Remap(XElement xml, ISymbol source, ISymbol destination)
    {
        static IReadOnlyList<string> Parameters(ISymbol symbol) => symbol switch
        {
            IMethodSymbol m => m.Parameters.Select(p => p.Name).ToArray(),
            IPropertySymbol p => p.Parameters.Select(p => p.Name).ToArray(), _ => []
        };
        static IReadOnlyList<string> TypeParameters(ISymbol symbol) => symbol switch
        {
            IMethodSymbol m => m.TypeParameters.Select(p => p.Name).ToArray(),
            INamedTypeSymbol t => t.TypeParameters.Select(p => p.Name).ToArray(), _ => []
        };
        static Dictionary<string, string> Map(IReadOnlyList<string> before, IReadOnlyList<string> after) => before.Count == after.Count
            ? before.Select((name, index) => (name, after[index])).ToDictionary(p => p.name, p => p.Item2, StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        var parameters = Map(Parameters(source), Parameters(destination));
        var generics = Map(TypeParameters(source), TypeParameters(destination));
        foreach (var element in xml.DescendantsAndSelf())
        {
            var map = element.Name.LocalName switch { "param" or "paramref" => parameters, "typeparam" or "typeparamref" => generics, _ => null };
            if (map is not null && element.Attribute("name") is { } name && map.TryGetValue(name.Value, out var replacement)) name.Value = replacement;
        }
    }
    private static XNode Clone(XNode node) => node switch
    {
        XElement element => new XElement(element), XCData data => new XCData(data.Value),
        XText text => new XText(text.Value), XComment comment => new XComment(comment.Value),
        _ => new XText(node.ToString())
    };
    private static IEnumerable<ISymbol> Targets(ISymbol symbol)
    {
        var overridden = symbol switch
        {
            IMethodSymbol m => (ISymbol?)m.OverriddenMethod, IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent, INamedTypeSymbol t => t.BaseType, _ => null
        };
        if (overridden is not null) yield return overridden;
        IEnumerable<ISymbol> explicitTargets = symbol switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations, IPropertySymbol p => p.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations, _ => []
        };
        foreach (var target in explicitTargets.OrderBy(t => t.GetDocumentationCommentId(), StringComparer.Ordinal)) yield return target;
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var @interface in type.Interfaces.OrderBy(i => i.ToDisplayString(), StringComparer.Ordinal)) yield return @interface;
        }
        else if (symbol.ContainingType is { } containing)
        {
            foreach (var target in containing.AllInterfaces.SelectMany(i => i.GetMembers()).OrderBy(m => m.GetDocumentationCommentId(), StringComparer.Ordinal))
                if (SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(target), symbol)) yield return target;
        }
    }
}
