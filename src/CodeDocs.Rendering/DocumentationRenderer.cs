using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeDocs.Model;

namespace CodeDocs.Rendering;

internal sealed class DocumentationRenderer(Func<string, string?> link, bool html)
{
    private static readonly string[] Sections = ["summary", "remarks", "param", "typeparam", "returns", "value", "exception", "example", "seealso", "inheritdoc"];
    public static string Summary(DocumentationComment comment)
    {
        if (string.IsNullOrWhiteSpace(comment.Xml)) return "";
        try { return Regex.Replace(string.Concat(XElement.Parse(comment.Xml).Element("summary")?.Nodes().Select(SummaryText) ?? []), @"\s+", " ").Trim(); }
        catch (System.Xml.XmlException) { return ""; }
    }
    private static string SummaryText(XNode node)
    {
        if (node is XText text) return text.Value;
        if (node is not XElement element) return "";
        if (element.Name.LocalName is "see" or "seealso")
        {
            if (!string.IsNullOrWhiteSpace(element.Value)) return element.Value;
            var cref = element.Attribute("cref")?.Value;
            return element.Attribute("langword")?.Value ?? (cref is { Length: > 2 } && cref[1] == ':' ? cref[2..] : cref)
                ?? element.Attribute("href")?.Value ?? "";
        }
        if (element.Name.LocalName is "paramref" or "typeparamref") return element.Attribute("name")?.Value ?? "";
        var content = string.Concat(element.Nodes().Select(SummaryText));
        return element.Name.LocalName == "para" ? " " + content + " " : content;
    }
    public string Render(DocumentationComment comment)
    {
        if (string.IsNullOrWhiteSpace(comment.Xml)) return "";
        var xml = XElement.Parse(comment.Xml);
        var result = new StringBuilder();
        foreach (var section in Sections)
        {
            var elements = xml.Elements(section).ToArray();
            if (elements.Length == 0) continue;
            var title = section switch
            {
                "summary" => "Summary", "param" => "Parameters", "typeparam" => "Type Parameters", "seealso" => "See Also",
                "inheritdoc" => "Inherited Documentation", _ => char.ToUpperInvariant(section[0]) + section[1..]
            };
            result.Append(html ? "<div class=\"doc-section\"><h4>" + E(title) + "</h4>" : "**" + title + "**\n\n");
            foreach (var element in elements)
            {
                if (section is "param" or "typeparam") result.Append(html ? "<p><code>" + E(element.Attribute("name")?.Value ?? "") + "</code> — " : "- `" + element.Attribute("name")?.Value + "` — ");
                if (section is "exception" or "seealso") result.Append(Reference(element, comment));
                if (section == "inheritdoc") result.Append(html ? "<p>Unresolved inheritdoc: " + E(element.ToString()) + "</p>" : "Unresolved inheritdoc: `" + element + "`\n\n");
                else result.Append(Nodes(element.Nodes(), comment));
                if (section is "param" or "typeparam") result.Append(html ? "</p>" : "\n");
                else result.Append(html ? "\n" : "\n\n");
            }
            result.Append(html ? "</div>" : "\n");
        }
        return result.ToString();
    }
    private string Nodes(IEnumerable<XNode> nodes, DocumentationComment comment) => string.Concat(nodes.Select(n => Node(n, comment)));
    private string Node(XNode node, DocumentationComment comment)
    {
        if (node is XText text) return html ? E(text.Value) : Md(text.Value);
        if (node is not XElement element) return "";
        var content = Nodes(element.Nodes(), comment);
        return element.Name.LocalName switch
        {
            "see" or "seealso" => Reference(element, comment),
            "paramref" or "typeparamref" => html ? "<code>" + E(element.Attribute("name")?.Value ?? "") + "</code>" : "`" + element.Attribute("name")?.Value + "`",
            "c" => html ? "<code>" + E(element.Value) + "</code>" : "<code>" + E(element.Value) + "</code>",
            "code" => html ? "<pre><code>" + E(element.Value.Trim('\r', '\n')) + "</code></pre>" : "\n\n" + Fence(element.Value.Trim('\r', '\n')) + "\n\n",
            "para" => html ? "<p>" + content + "</p>" : "\n\n" + content + "\n\n",
            "list" => List(element, comment),
            "br" => html ? "<br>" : "\n",
            _ => content
        };
    }
    private string List(XElement element, DocumentationComment comment)
    {
        var ordered = element.Attribute("type")?.Value == "number";
        var rows = element.Elements().Where(e => e.Name.LocalName is "item" or "listheader").Select(item =>
            string.Join(" — ", item.Elements().Select(e => Nodes(e.Nodes(), comment)))).ToArray();
        if (!html) return "\n" + string.Join("\n", rows.Select((r, i) => (ordered ? (i + 1) + ". " : "- ") + r)) + "\n\n";
        var tag = ordered ? "ol" : "ul";
        return "<" + tag + ">" + string.Concat(rows.Select(r => "<li>" + r + "</li>")) + "</" + tag + ">";
    }
    private string Reference(XElement element, DocumentationComment comment)
    {
        var cref = element.Attribute("cref")?.Value;
        var href = element.Attribute("href")?.Value;
        var label = element.Value.Trim();
        if (label.Length == 0) label = element.Attribute("langword")?.Value ?? cref?.Replace("T:", "").Replace("M:", "").Replace("P:", "").Replace("F:", "").Replace("E:", "") ?? href ?? "";
        string? target = cref is not null && comment.References.TryGetValue(cref, out var id) ? link(id) : null;
        if (target is null && href is not null && Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "mailto") target = href;
        return target is null ? (html ? E(label) : Md(label)) : html ? "<a href=\"" + E(target) + "\">" + E(label) + "</a>" : "[" + Md(label) + "](" + target.Replace(" ", "%20").Replace(")", "%29") + ")";
    }
    internal static string E(string text) => WebUtility.HtmlEncode(text);
    internal static string Md(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "\\|");
    internal static string Fence(string text, string language = "")
    {
        var longest = Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + language + "\n" + text + "\n" + fence;
    }
}
