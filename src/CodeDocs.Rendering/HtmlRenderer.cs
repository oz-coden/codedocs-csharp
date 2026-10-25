using System.Text;
using System.Net;
using System.Text.Json;
using CodeDocs.Model;
using static CodeDocs.Rendering.DocumentationContent;
using static CodeDocs.Rendering.RenderOutput;
using static CodeDocs.Rendering.HtmlMarkup;
using static CodeDocs.Rendering.HtmlAssets;

namespace CodeDocs.Rendering;

public static class HtmlRenderer
{
    private static readonly JsonSerializerOptions SearchOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public static void Render(SolutionDocument doc, BuildOptions options, string directory)
    {
        var links = new SymbolLinks(doc, t => "types/" + t.Anchor + ".html");
        string? RootLink(string id) => links.Resolve(id);
        var body = new StringBuilder(Warning(doc));
        body.Append("<h1>" + E(doc.Name) + " API</h1><p class=\"muted\">" + E(doc.Input) + " · " + doc.Projects.Count + " projects · " + doc.Types.Count + " types</p>");
        body.Append("<h2 id=\"projects\">Projects</h2><ul>");
        foreach (var project in doc.Projects)
            body.Append("<li>" + Linked(project.Name, "projects/" + StableId.Anchor(project.Id) + ".html") + " <span class=\"muted\">" + E(project.TargetFramework ?? "") + "</span></li>");
        body.Append("</ul><h2 id=\"namespaces\">Namespaces</h2><ul>");
        foreach (var ns in doc.Namespaces)
            body.Append("<li>" + Linked(NamespaceName(ns.Name), NamespaceFile(ns.Name)) + "</li>");
        body.Append("</ul><h2 id=\"types\">Types</h2>" + TypeTable(doc.Types, RootLink));
        Write(directory, "index.html", HtmlPage(doc.Name, body.ToString(), "", options.Search));
        foreach (var project in doc.Projects)
        {
            body = new(Warning(doc));
            body.Append("<h1>" + E(project.Name) + "</h1><p>Project: <code>" + E(project.File) + "</code></p><p>Assembly: " + E(project.AssemblyName) + " · " + E(project.TargetFramework ?? "") + " · C# " + E(project.LanguageVersion) + " · Nullable: " + E(project.NullableContext) + "</p>");
            body.Append(TypeTable(doc.Types.Where(t => t.ProjectId == project.Id), id => links.Resolve(id, "../")));
            body.Append(Attributes(project.Attributes, id => links.Resolve(id, "../")));
            Write(directory, "projects/" + StableId.Anchor(project.Id) + ".html", HtmlPage(project.Name, body.ToString(), "../", options.Search));
        }

        foreach (var ns in doc.Namespaces)
        {
            body = new(Warning(doc));
            body.Append("<h1>" + E(NamespaceName(ns.Name)) + "</h1>");
            foreach (var group in doc.Types.Where(t => t.Namespace == ns.Name).GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                body.Append("<h2>" + E(group.Key) + "</h2>" + TypeTable(group, id => links.Resolve(id, "../")));
            Write(directory, NamespaceFile(ns.Name), HtmlPage(NamespaceName(ns.Name), body.ToString(), "../", options.Search));
        }

        foreach (var type in doc.Types)
            Write(directory, "types/" + type.Anchor + ".html", HtmlPage(type.Name, TypeContent(doc, type, options, id => links.Resolve(id, "../")), "../", options.Search));
        Write(directory, "assets/site.css", Css);
        if (options.Search)
        {
            var entries = doc.Namespaces.Select(ns => new SearchEntry(NamespaceName(ns.Name), "Namespace", "", NamespaceFile(ns.Name)))
                .Concat(doc.Types.Select(t => new SearchEntry(t.Name, t.Namespace, Summary(t.Documentation), links[t.Id])))
                .Concat(doc.Types.SelectMany(t => t.Members.Select(m => new SearchEntry(
                    string.Concat(m.Declaration.Select(p => p.Text)), t.Name, Summary(m.Documentation), links[m.Id])))).ToArray();
            var json = JsonSerializer.Serialize(entries, SearchOptions);
            Write(directory, "search-index.json", json);
            Write(directory, "assets/search-index.js", "window.CODEDOCS_SEARCH = " + json + ";");
            Write(directory, "assets/search.js", SearchScript);
        }
    }

    private static string Linked(string label, string? target) => target is null ? E(label) : "<a href=\"" + E(target) + "\">" + E(label) + "</a>";
    private static string Declaration(SymbolDocument symbol, Func<string, string?> link) => "<pre class=\"declaration\"><code>" + string.Concat(symbol.Declaration.Select(p => Linked(p.Text, p.TargetId is null ? null : link(p.TargetId)))) + "</code></pre>";
    private static string NamespaceFile(string ns) => "namespaces/" + StableId.Anchor("N:" + ns) + ".html";
    private static string Warning(SolutionDocument doc) => doc.State == AnalysisState.Partial ? "<aside class=\"warning\" role=\"alert\">Warning: generated from an incomplete compilation. Some API information may be missing or unresolved.</aside>" : "";
    private static string HtmlPage(string title, string body, string prefix, bool search) => "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + E(title) + " — CodeDocs.CSharp</title><link rel=\"stylesheet\" href=\"" + prefix + "assets/site.css\"></head><body><header><a class=\"brand\" href=\"" + prefix + "index.html\">CodeDocs.CSharp</a><nav aria-label=\"Main\"><a href=\"" + prefix + "index.html#projects\">Projects</a><a href=\"" + prefix + "index.html#namespaces\">Namespaces</a><a href=\"" + prefix + "index.html#types\">Types</a></nav></header>" +
        (search ? "<div class=\"search\"><label for=\"search\">Search API</label><input id=\"search\" type=\"search\" placeholder=\"Namespace, type, member, summary…\" autocomplete=\"off\"><div id=\"search-status\" aria-live=\"polite\"></div><ul id=\"search-results\"></ul></div>" : "") +
        "<main>" + body + "</main><footer>Generated by CodeDocs.CSharp</footer>" +
        (search ? "<script src=\"" + prefix + "assets/search-index.js\"></script><script src=\"" + prefix + "assets/search.js\" data-root=\"" + prefix + "\"></script>" : "") + "</body></html>\n";
    private sealed record SearchEntry(string Name, string Scope, string Summary, string Url);
    private static string TypeTable(IEnumerable<TypeDocument> types, Func<string, string?> link)
    {
        var rows = Presentation.OrderTypes(types).Select(t => "<tr><th scope=\"row\">" + Linked(t.Name, link(t.Id)) + "</th><td>" + E(Summary(t.Documentation)) + "</td></tr>");
        return "<table><thead><tr><th>Type</th><th>Description</th></tr></thead><tbody>" + string.Concat(rows) + "</tbody></table>";
    }

    private static string TypeContent(SolutionDocument doc, TypeDocument type, BuildOptions options, Func<string, string?> link)
    {
        var b = new StringBuilder(Warning(doc));
        string Heading(string text, int level = 2) => $"<h{level}>" + E(text) + $"</h{level}>";
        string Paragraph(string text) => "<p>" + text + "</p>";
        b.Append("<h1 id=\"" + type.Anchor + "\">" + E(type.Name) + "</h1>");
        b.Append(Paragraph(E(NamespaceName(type.Namespace)) + " · " + E(doc.Projects.First(p => p.Id == type.ProjectId).Name)));
        b.Append(Declaration(type, link));
        var docs = new HtmlDocumentationRenderer(link);
        b.Append(docs.Render(type.Documentation));
        if (type.BaseType is not null)
            b.Append(Heading("Inheritance") + Paragraph(Linked(type.BaseType.Display, type.BaseType.TargetId is null ? null : link(type.BaseType.TargetId))));
        if (type.Interfaces.Count > 0)
            b.Append(Heading("Implemented Interfaces") + List(type.Interfaces.Select(i => Linked(i.Display, i.TargetId is null ? null : link(i.TargetId)))));
        b.Append(Generics(type.GenericParameters, link));
        b.Append(Attributes(type.Attributes, link));
        if (type.DelegateSignature is { } signature)
            b.Append(MemberMetadata(signature, link));
        if (options.ShowDeclarationFiles)
            b.Append(Sources(type));
        b.Append(Members(type, options, link));
        return b.ToString();
    }

    private static string Members(TypeDocument type, BuildOptions options, Func<string, string?> link)
    {
        var b = new StringBuilder();
        var docs = new HtmlDocumentationRenderer(link);
        string Heading(string text, int level = 2) => $"<h{level}>" + E(text) + $"</h{level}>";
        string Paragraph(string text) => "<p>" + text + "</p>";
        var groups = Presentation.MemberGroups(type);
        if (groups.Count > 0)
            b.Append(Heading("Member Summary"));
        foreach (var(category, members)in groups)
        {
            b.Append(Heading(category, 3));
            b.Append("<table><thead><tr><th>Member</th><th>Description</th></tr></thead><tbody>");
            foreach (var member in members)
            {
                var name = string.Concat(member.Declaration.Select(p => p.Text));
                var summary = Summary(member.Documentation);
                b.Append("<tr><th scope=\"row\"><code>" + Linked(name, "#" + member.Anchor) + "</code></th><td>" + E(summary) + "</td></tr>");
            }

            b.Append("</tbody></table>");
        }

        foreach (var group in type.InheritedMembers.GroupBy(m => (m.DeclaringType, m.TypeId, m.Category)))
        {
            b.Append(Heading(group.Key.Category + " inherited from " + group.Key.DeclaringType, 3));
            b.Append(List(group.Select(m => Linked(m.Name, m.MemberId is null ? null : link(m.MemberId)))));
        }

        foreach (var(category, members)in groups)
        {
            b.Append(Heading(category + " Details"));
            foreach (var member in members)
            {
                b.Append("<article id=\"" + member.Anchor + "\"><h3>" + E(member.Name) + "</h3>");
                b.Append(Declaration(member, link));
                if (member.IsPrimaryConstructor)
                    b.Append(Paragraph("Primary constructor."));
                b.Append(docs.Render(member.Documentation));
                b.Append(Generics(member.GenericParameters, link));
                b.Append(MemberMetadata(member, link));
                if (options.ShowDeclarationFiles)
                    b.Append(Sources(member));
                b.Append("</article>");
            }
        }

        return b.ToString();
    }

    private static string List(IEnumerable<string> items) => "<ul>" + string.Concat(items.Select(i => "<li>" + i + "</li>")) + "</ul>";
    private static string Generics(IReadOnlyList<GenericParameterDocument> parameters, Func<string, string?> link)
    {
        if (parameters.Count == 0)
            return "";
        var b = new StringBuilder("<h3>Generic Parameters</h3>");
        foreach (var parameter in parameters)
        {
            var name = (parameter.Variance == "None" ? "" : parameter.Variance.ToLowerInvariant() + " ") + parameter.Name;
            b.Append("<h4><code>" + E(name) + "</code></h4>");
            b.Append(List(parameter.ConstraintDetails.Count > 0 ? parameter.ConstraintDetails.Select(c => string.Concat(c.Declaration.Select(p => Linked(p.Text, p.TargetId is null ? null : link(p.TargetId))))) : parameter.ConstraintItems.Select(c => E(c))));
            b.Append(Attributes(parameter.Attributes, link));
        }

        return b.ToString();
    }

    private static string MemberMetadata(MemberDocument member, Func<string, string?> link)
    {
        string Heading(string name) => "<h4>" + E(name) + "</h4>";
        var b = new StringBuilder(Attributes(member.Attributes, link));
        foreach (var parameter in member.Parameters.Where(p => p.Attributes.Count > 0))
            b.Append(Heading("Attributes on parameter " + parameter.Name) + Attributes(parameter.Attributes, link, false));
        foreach (var accessor in member.AccessorDetails)
        {
            if (accessor.Attributes.Count > 0)
                b.Append(Heading("Attributes on " + accessor.Kind) + Attributes(accessor.Attributes, link, false));
            foreach (var parameter in accessor.Parameters.Where(p => p.Attributes.Count > 0))
                b.Append(Heading("Attributes on " + accessor.Kind + " parameter " + parameter.Name) + Attributes(parameter.Attributes, link, false));
        }

        return b.ToString();
    }

    private static string Attributes(IReadOnlyList<AttributeDocument> attributes, Func<string, string?> link, bool heading = true)
    {
        if (attributes.Count == 0)
            return "";
        var title = heading ? "<h3>Attributes</h3>" : "";
        return title + List(attributes.Select(a => (a.Target is "return" or "module" or "field" ? a.Target + ": " : "") + Linked(a.Display, a.Type?.TargetId is { } id ? link(id) : null)));
    }

    private static string Sources(SymbolDocument symbol)
    {
        if (symbol.Declarations.Count == 0)
            return "";
        return ("<details><summary>Declared in</summary>") + List(symbol.Declarations.Select(s => ("<code>" + E(s.File) + ":" + s.Line + "</code>") + (s.Generated ? " (generated)" : ""))) + ("</details>");
    }
}

internal static class HtmlMarkup
{
    internal static string E(string text) => WebUtility.HtmlEncode(text);
}
