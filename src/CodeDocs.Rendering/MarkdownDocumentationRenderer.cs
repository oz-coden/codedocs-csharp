using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeDocs.Model;
using static CodeDocs.Rendering.DocumentationContent;
using static CodeDocs.Rendering.MarkdownMarkup;

namespace CodeDocs.Rendering;

internal sealed class MarkdownDocumentationRenderer(Func<string, string?> link)
{
    public string Render(DocumentationComment comment)
    {
        if (Parse(comment) is not { } xml)
            return "";
        var result = new StringBuilder();
        foreach (var section in Sections)
        {
            var elements = xml.Elements(section).ToArray();
            if (elements.Length == 0)
                continue;
            var title = SectionTitle(section);
            result.Append("**" + title + "**\n\n");
            foreach (var element in elements)
            {
                if (section is "param" or "typeparam")
                    result.Append("- `" + element.Attribute("name")?.Value + "` — ");
                if (section is "exception" or "seealso")
                {
                    var reference = new XElement(element.Name, element.Attributes());
                    result.Append(Reference(reference, comment));
                    if (element.Nodes().Any())
                        result.Append(" — ");
                }

                if (section == "inheritdoc")
                    result.Append("Unresolved inheritdoc: `" + element + "`\n\n");
                else
                    result.Append(Nodes(element.Nodes(), comment));
                if (section is "param" or "typeparam")
                    result.Append("\n");
                else
                    result.Append("\n\n");
            }

            result.Append("\n");
        }

        return result.ToString();
    }

    private string Nodes(IEnumerable<XNode> nodes, DocumentationComment comment) => string.Concat(nodes.Select(n => Node(n, comment)));
    private string Node(XNode node, DocumentationComment comment)
    {
        if (node is XText text)
            return Md(text.Value);
        if (node is not XElement element)
            return "";
        var content = Nodes(element.Nodes(), comment);
        return element.Name.LocalName switch
        {
            "see" or "seealso" => Reference(element, comment),
            "paramref" or "typeparamref" => "`" + element.Attribute("name")?.Value + "`",
            "c" => "<code>" + System.Net.WebUtility.HtmlEncode(element.Value) + "</code>",
            "code" => "\n\n" + Fence(element.Value.Trim('\r', '\n')) + "\n\n",
            "para" => "\n\n" + content + "\n\n",
            "list" => List(element, comment),
            "br" => "\n",
            _ => content
        };
    }

    private string List(XElement element, DocumentationComment comment)
    {
        if (element.Attribute("type")?.Value == "table")
            return Table(element, comment);
        var ordered = element.Attribute("type")?.Value == "number";
        var rows = element.Elements().Where(e => e.Name.LocalName is "item" or "listheader").Select(item => string.Join(" — ", ListItemParts(item).Select(nodes => Nodes(nodes, comment)))).ToArray();
        return "\n" + string.Join("\n", rows.Select((r, i) => (ordered ? (i + 1) + ". " : "- ") + r)) + "\n\n";
    }

    private string Table(XElement element, DocumentationComment comment)
    {
        var rows = element.Elements("item").Select(item => item.Elements().Select(e => Nodes(e.Nodes(), comment)).ToArray()).ToArray();
        var headings = element.Element("listheader")?.Elements().Select(e => Nodes(e.Nodes(), comment)).ToArray() ?? [];
        var columns = Math.Max(headings.Length, rows.Select(r => r.Length).DefaultIfEmpty(0).Max());
        if (columns == 0)
            return "";
        if (headings.Length == 0)
            headings = Enumerable.Range(0, columns).Select(i => i == 0 ? "Term" : i == 1 ? "Description" : "Value").ToArray();
        string Row(IReadOnlyList<string> values) => "| " + string.Join(" | ", Enumerable.Range(0, columns).Select(i => (values.ElementAtOrDefault(i) ?? "").Trim().Replace("\r", "").Replace("\n", "<br>"))) + " |";
        return "\n\n" + Row(headings) + "\n" + Row(Enumerable.Repeat("---", columns).ToArray()) + "\n" + string.Join("\n", rows.Select(Row)) + "\n\n";
    }

    private string Reference(XElement element, DocumentationComment comment)
    {
        var (label, target) = ResolveReference(element, comment, link);
        return target is null ? Md(label) : "[" + Md(label) + "](" + target.Replace(" ", "%20").Replace(")", "%29") + ")";
    }
}
