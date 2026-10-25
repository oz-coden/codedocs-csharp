using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeDocs.Model;

namespace CodeDocs.Rendering;
// XML selection, summaries and reference resolution have no HTML/Markdown rules.
internal static class DocumentationContent
{
    internal static readonly string[] Sections = ["summary", "remarks", "param", "typeparam", "returns", "value", "exception", "example", "seealso", "inheritdoc"];
    internal static string NamespaceName(string ns) => ns.Length == 0 ? "(global namespace)" : ns;
    public static string Summary(DocumentationComment comment)
    {
        return Regex.Replace(string.Concat(Parse(comment)?.Element("summary")?.Nodes().Select(SummaryText) ?? []), @"\s+", " ").Trim();
    }

    private static string SummaryText(XNode node)
    {
        if (node is XText text)
            return text.Value;
        if (node is not XElement element)
            return "";
        if (element.Name.LocalName is "see" or "seealso")
        {
            if (!string.IsNullOrWhiteSpace(element.Value))
                return element.Value;
            var cref = element.Attribute("cref")?.Value;
            return element.Attribute("langword")?.Value ?? (cref is { Length: > 2 } && cref[1] == ':' ? cref[2..] : cref) ?? element.Attribute("href")?.Value ?? "";
        }

        if (element.Name.LocalName is "paramref" or "typeparamref")
            return element.Attribute("name")?.Value ?? "";
        var content = string.Concat(element.Nodes().Select(SummaryText));
        return element.Name.LocalName == "para" ? " " + content + " " : content;
    }

    internal static XElement? Parse(DocumentationComment comment)
    {
        if (string.IsNullOrWhiteSpace(comment.Xml))
            return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(comment.Xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    internal static IEnumerable<IEnumerable<XNode>> ListItemParts(XElement item)
    {
        // Standard term/description pairs use separators. Mixed inline content keeps its own nodes.
        if (item.HasElements && item.Elements().All(e => e.Name.LocalName is "term" or "description") &&
            item.Nodes().OfType<XText>().All(t => string.IsNullOrWhiteSpace(t.Value)))
            return item.Elements().Select(e => e.Nodes());
        return [item.Nodes()];
    }

    internal static string SectionTitle(string section) => section switch
    {
        "summary" => "Summary",
        "param" => "Parameters",
        "typeparam" => "Type Parameters",
        "seealso" => "See Also",
        "inheritdoc" => "Inherited Documentation",
        _ => char.ToUpperInvariant(section[0]) + section[1..]
    };
    internal static (string Label, string? Target) ResolveReference(XElement element, DocumentationComment comment, Func<string, string?> link)
    {
        var cref = element.Attribute("cref")?.Value;
        var href = element.Attribute("href")?.Value;
        var label = element.Value.Trim();
        if (label.Length == 0)
            label = element.Attribute("langword")?.Value ?? cref?.Replace("T:", "").Replace("M:", "").Replace("P:", "").Replace("F:", "").Replace("E:", "") ?? href ?? "";
        string? target = cref is not null && comment.References.TryGetValue(cref, out var id) ? link(id) : null;
        if (target is null && href is not null && Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto")
            target = href;
        return (label, target);
    }
}
