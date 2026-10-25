using System.Text;
using System.Xml.Linq;
using CodeDocs.Model;
using static CodeDocs.Rendering.DocumentationContent;
using static CodeDocs.Rendering.HtmlMarkup;

namespace CodeDocs.Rendering;

internal sealed class HtmlDocumentationRenderer(Func<string, string?> link)
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
            result.Append("<div class=\"doc-section\"><h4>" + E(title) + "</h4>");
            foreach (var element in elements)
            {
                if (section is "param" or "typeparam")
                    result.Append("<p><code>" + E(element.Attribute("name")?.Value ?? "") + "</code> — ");
                if (section is "exception" or "seealso")
                {
                    var reference = new XElement(element.Name, element.Attributes());
                    result.Append(Reference(reference, comment));
                    if (element.Nodes().Any())
                        result.Append(" — ");
                }

                if (section == "inheritdoc")
                    result.Append("<p>Unresolved inheritdoc: " + E(element.ToString()) + "</p>");
                else
                    result.Append(Nodes(element.Nodes(), comment));
                if (section is "param" or "typeparam")
                    result.Append("</p>");
                else
                    result.Append("\n");
            }

            result.Append("</div>");
        }

        return result.ToString();
    }

    private string Nodes(IEnumerable<XNode> nodes, DocumentationComment comment) => string.Concat(nodes.Select(n => Node(n, comment)));
    private string Node(XNode node, DocumentationComment comment)
    {
        if (node is XText text)
            return E(text.Value);
        if (node is not XElement element)
            return "";
        var content = Nodes(element.Nodes(), comment);
        return element.Name.LocalName switch
        {
            "see" or "seealso" => Reference(element, comment),
            "paramref" or "typeparamref" => "<code>" + E(element.Attribute("name")?.Value ?? "") + "</code>",
            "c" => "<code>" + E(element.Value) + "</code>",
            "code" => "<pre><code>" + E(element.Value.Trim('\r', '\n')) + "</code></pre>",
            "para" => "<p>" + content + "</p>",
            "list" => List(element, comment),
            "br" => "<br>",
            _ => content
        };
    }

    private string List(XElement element, DocumentationComment comment)
    {
        if (element.Attribute("type")?.Value == "table")
            return Table(element, comment);
        var ordered = element.Attribute("type")?.Value == "number";
        var rows = element.Elements().Where(e => e.Name.LocalName is "item" or "listheader").Select(item => string.Join(" — ", ListItemParts(item).Select(nodes => Nodes(nodes, comment)))).ToArray();
        var tag = ordered ? "ol" : "ul";
        return "<" + tag + ">" + string.Concat(rows.Select(r => "<li>" + r + "</li>")) + "</" + tag + ">";
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
        return "<table class=\"doc-table\"><thead><tr>" + string.Concat(Enumerable.Range(0, columns).Select(i => "<th>" + headings.ElementAtOrDefault(i) + "</th>")) + "</tr></thead><tbody>" + string.Concat(rows.Select(row => "<tr>" + string.Concat(Enumerable.Range(0, columns).Select(i => "<td>" + row.ElementAtOrDefault(i) + "</td>")) + "</tr>")) + "</tbody></table>";
    }

    private string Reference(XElement element, DocumentationComment comment)
    {
        var (label, target) = ResolveReference(element, comment, link);
        return target is null ? E(label) : "<a href=\"" + E(target) + "\">" + E(label) + "</a>";
    }
}
