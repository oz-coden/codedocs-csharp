using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeDocs.Model;
using static CodeDocs.Rendering.DocumentationRenderer;

namespace CodeDocs.Rendering;

public static class SiteRenderer
{
    public static readonly string[] Categories = ["Constructors", "Fields", "Enum Members", "Properties", "Indexers", "Events", "Methods", "Explicit Interface Implementations", "Operators", "Conversions"];
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task RenderAsync(SolutionDocument document, BuildOptions options, string destination, CancellationToken cancellationToken = default)
    {
        var multiple = options.Formats.Count > 1;
        foreach (var format in options.Formats)
        {
            var directory = multiple ? Path.Combine(destination, format) : destination;
            Directory.CreateDirectory(directory);
            switch (format)
            {
                case "html": RenderHtml(document, options, directory); break;
                case "markdown": RenderMarkdown(document, options, directory); break;
                case "json": Write(directory, "api.json", JsonSerializer.Serialize(document, JsonOptions)); break;
            }
        }
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }
    private static void Write(string directory, string file, string content)
    {
        var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n").TrimEnd() + "\n", new UTF8Encoding(false));
    }
    private static Dictionary<string, string> Links(SolutionDocument doc, bool html, bool single)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in doc.Types)
        {
            var path = single ? "" : "types/" + type.Anchor + (html ? ".html" : ".md");
            links[type.Id] = path + "#" + type.Anchor;
            foreach (var member in type.Members) links[member.Id] = path + "#" + member.Anchor;
        }
        return links;
    }
    private static string? Link(Dictionary<string, string> links, string id, string prefix = "") => links.TryGetValue(id, out var url) ? prefix + url : null;
    private static string Linked(string label, string? target, bool html) => target is null ? (html ? E(label) : Md(label)) : html
        ? "<a href=\"" + E(target) + "\">" + E(label) + "</a>" : "[" + Md(label) + "](" + target + ")";
    private static string Declaration(SymbolDocument symbol, Func<string, string?> link, bool html) => html
        ? "<pre class=\"declaration\"><code>" + string.Concat(symbol.Declaration.Select(p => Linked(p.Text, p.TargetId is null ? null : link(p.TargetId), true))) + "</code></pre>"
        : Fence(string.Concat(symbol.Declaration.Select(p => p.Text)), "csharp") + "\n\n" +
          string.Join(" · ", symbol.Declaration.Where(p => p.TargetId is not null).Select(p => (p.Text, Id: p.TargetId!)).DistinctBy(p => p.Id)
              .Where(p => link(p.Id) is not null).Select(p => Linked(p.Text, link(p.Id), false))) + "\n\n";
    private static string NamespaceName(string ns) => ns.Length == 0 ? "(global namespace)" : ns;
    private static string NamespaceFile(string ns, bool html) => "namespaces/" + StableId.Anchor("N:" + ns) + (html ? ".html" : ".md");
    private static string Warning(SolutionDocument doc, bool html) => doc.State == AnalysisState.Partial
        ? html ? "<aside class=\"warning\" role=\"alert\">Warning: generated from an incomplete compilation. Some API information may be missing or unresolved.</aside>" : "> **Warning:** Generated from an incomplete compilation. Some API information may be missing or unresolved.\n\n"
        : "";

    private static string HtmlPage(string title, string body, string prefix, bool search) => "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + E(title) + " — CodeDocs.CSharp</title><link rel=\"stylesheet\" href=\"" + prefix + "assets/site.css\"></head><body><header><a class=\"brand\" href=\"" + prefix + "index.html\">CodeDocs.CSharp</a><nav aria-label=\"Main\"><a href=\"" + prefix + "index.html#projects\">Projects</a><a href=\"" + prefix + "index.html#namespaces\">Namespaces</a><a href=\"" + prefix + "index.html#types\">Types</a></nav></header>" +
        (search ? "<div class=\"search\"><label for=\"search\">Search API</label><input id=\"search\" type=\"search\" placeholder=\"Namespace, type, member, summary…\" autocomplete=\"off\"><div id=\"search-status\" aria-live=\"polite\"></div><ul id=\"search-results\"></ul></div>" : "") +
        "<main>" + body + "</main><footer>Generated by CodeDocs.CSharp</footer>" +
        (search ? "<script src=\"" + prefix + "assets/search-index.js\"></script><script src=\"" + prefix + "assets/search.js\" data-root=\"" + prefix + "\"></script>" : "") + "</body></html>\n";

    private static void RenderHtml(SolutionDocument doc, BuildOptions options, string directory)
    {
        var links = Links(doc, true, false);
        string? RootLink(string id) => Link(links, id);
        var body = new StringBuilder(Warning(doc, true));
        body.Append("<h1>" + E(doc.Name) + " API</h1><p class=\"muted\">" + E(doc.Input) + " · " + doc.Projects.Count + " projects · " + doc.Types.Count + " types</p>");
        body.Append("<h2 id=\"projects\">Projects</h2><ul>");
        foreach (var project in doc.Projects)
            body.Append("<li>" + Linked(project.Name, "projects/" + StableId.Anchor(project.Id) + ".html", true) + " <span class=\"muted\">" + E(project.TargetFramework ?? "") + "</span></li>");
        body.Append("</ul><h2 id=\"namespaces\">Namespaces</h2><ul>");
        foreach (var ns in doc.Namespaces) body.Append("<li>" + Linked(NamespaceName(ns.Name), NamespaceFile(ns.Name, true), true) + "</li>");
        body.Append("</ul><h2 id=\"types\">Types</h2>" + TypeTable(doc.Types, RootLink, true));
        Write(directory, "index.html", HtmlPage(doc.Name, body.ToString(), "", options.Search));
        foreach (var project in doc.Projects)
        {
            body = new(Warning(doc, true));
            body.Append("<h1>" + E(project.Name) + "</h1><p>Project: <code>" + E(project.File) + "</code></p><p>Assembly: " + E(project.AssemblyName) + " · " + E(project.TargetFramework ?? "") + " · C# " + E(project.LanguageVersion) + " · Nullable: " + E(project.NullableContext) + "</p>");
            body.Append(TypeTable(doc.Types.Where(t => t.ProjectId == project.Id), id => Link(links, id, "../"), true));
            body.Append(Attributes(project.Attributes, id => Link(links, id, "../"), true));
            Write(directory, "projects/" + StableId.Anchor(project.Id) + ".html", HtmlPage(project.Name, body.ToString(), "../", options.Search));
        }
        foreach (var ns in doc.Namespaces)
        {
            body = new(Warning(doc, true)); body.Append("<h1>" + E(NamespaceName(ns.Name)) + "</h1>");
            foreach (var group in doc.Types.Where(t => t.Namespace == ns.Name).GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                body.Append("<h2>" + E(group.Key) + "</h2>" + TypeTable(group, id => Link(links, id, "../"), true));
            Write(directory, NamespaceFile(ns.Name, true), HtmlPage(NamespaceName(ns.Name), body.ToString(), "../", options.Search));
        }
        foreach (var type in doc.Types)
            Write(directory, "types/" + type.Anchor + ".html", HtmlPage(type.Name,
                TypeContent(doc, type, options, id => Link(links, id, "../"), true), "../", options.Search));
        Write(directory, "assets/site.css", Css);
        if (options.Search)
        {
            var entries = doc.Namespaces.Select(ns => new SearchEntry(NamespaceName(ns.Name), "Namespace", "", NamespaceFile(ns.Name, true)))
                .Concat(doc.Types.Select(t => new SearchEntry(t.Name, t.Namespace, Summary(t.Documentation), links[t.Id])))
                .Concat(doc.Types.SelectMany(t => t.Members.Select(m => new SearchEntry(string.Concat(m.Declaration.Select(p => p.Text)), t.Name, Summary(m.Documentation), links[m.Id])))).ToArray();
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            Write(directory, "search-index.json", json);
            Write(directory, "assets/search-index.js", "window.CODEDOCS_SEARCH = " + json + ";");
            Write(directory, "assets/search.js", SearchScript);
        }
    }
    private sealed record SearchEntry(string Name, string Scope, string Summary, string Url);
    private static string TypeTable(IEnumerable<TypeDocument> types, Func<string, string?> link, bool html)
    {
        var rows = types.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t =>
            html ? "<tr><th scope=\"row\">" + Linked(t.Name, link(t.Id), true) + "</th><td>" + E(Summary(t.Documentation)) + "</td></tr>"
                : "- " + Linked(t.Name, link(t.Id), false) + (Summary(t.Documentation) is { Length: > 0 } summary ? " — " + Md(summary) : ""));
        return html ? "<table><thead><tr><th>Type</th><th>Description</th></tr></thead><tbody>" + string.Concat(rows) + "</tbody></table>" : string.Join("\n", rows) + "\n\n";
    }

    private static string TypeContent(SolutionDocument doc, TypeDocument type, BuildOptions options, Func<string, string?> link, bool html)
    {
        var b = new StringBuilder(Warning(doc, html));
        string Heading(string text, int level = 2) => html ? $"<h{level}>" + E(text) + $"</h{level}>" : new string('#', level) + " " + Md(text) + "\n\n";
        string Paragraph(string text) => html ? "<p>" + text + "</p>" : text + "\n\n";
        b.Append(html ? "<h1 id=\"" + type.Anchor + "\">" + E(type.Name) + "</h1>" : "<a id=\"" + type.Anchor + "\"></a>\n\n# " + Md(type.Name) + "\n\n");
        b.Append(Paragraph((html ? E(NamespaceName(type.Namespace)) : Md(NamespaceName(type.Namespace))) + " · " +
            (html ? E(doc.Projects.First(p => p.Id == type.ProjectId).Name) : Md(doc.Projects.First(p => p.Id == type.ProjectId).Name))));
        b.Append(Declaration(type, link, html));
        var docs = new DocumentationRenderer(link, html);
        b.Append(docs.Render(type.Documentation));
        if (type.BaseType is not null)
            b.Append(Heading("Inheritance") + Paragraph(Linked(type.BaseType.Display, type.BaseType.TargetId is null ? null : link(type.BaseType.TargetId), html)));
        if (type.Interfaces.Count > 0)
            b.Append(Heading("Implemented Interfaces") + List(type.Interfaces.Select(i => Linked(i.Display, i.TargetId is null ? null : link(i.TargetId), html)), html));
        b.Append(Generics(type.GenericParameters, link, html));
        b.Append(Attributes(type.Attributes, link, html));
        if (type.DelegateSignature is { } signature)
            b.Append(MemberMetadata(signature, link, html));
        if (options.ShowDeclarationFiles) b.Append(Sources(type, html));
        var categories = Categories.Where(c => type.Members.Any(m => m.Category == c)).ToArray();
        if (categories.Length > 0) b.Append(Heading("Member Summary"));
        foreach (var category in categories)
        {
            b.Append(Heading(category, 3));
            if (html) b.Append("<table><thead><tr><th>Member</th><th>Description</th></tr></thead><tbody>");
            foreach (var member in type.Members.Where(m => m.Category == category))
            {
                var name = string.Concat(member.Declaration.Select(p => p.Text));
                var summary = Summary(member.Documentation);
                b.Append(html ? "<tr><th scope=\"row\"><code>" + Linked(name, "#" + member.Anchor, true) + "</code></th><td>" + E(summary) + "</td></tr>"
                    : "- " + Linked(name, "#" + member.Anchor, false) + (summary.Length > 0 ? " — " + Md(summary) : "") + "\n");
            }
            b.Append(html ? "</tbody></table>" : "\n");
        }
        foreach (var group in type.InheritedMembers.GroupBy(m => (m.DeclaringType, m.TypeId, m.Category)))
        {
            b.Append(Heading(group.Key.Category + " inherited from " + group.Key.DeclaringType, 3));
            b.Append(List(group.Select(m => Linked(m.Name, m.MemberId is null ? null : link(m.MemberId), html)), html));
        }
        foreach (var category in categories)
        {
            b.Append(Heading(category + " Details"));
            foreach (var member in type.Members.Where(m => m.Category == category))
            {
                b.Append(html ? "<article id=\"" + member.Anchor + "\"><h3>" + E(member.Name) + "</h3>"
                    : "<a id=\"" + member.Anchor + "\"></a>\n\n### " + Md(member.Name) + "\n\n");
                b.Append(Declaration(member, link, html));
                if (member.IsPrimaryConstructor) b.Append(Paragraph("Primary constructor."));
                b.Append(docs.Render(member.Documentation));
                b.Append(Generics(member.GenericParameters, link, html));
                b.Append(MemberMetadata(member, link, html));
                if (options.ShowDeclarationFiles) b.Append(Sources(member, html));
                if (html) b.Append("</article>");
            }
        }
        return b.ToString();
    }
    private static string List(IEnumerable<string> items, bool html) => html ? "<ul>" + string.Concat(items.Select(i => "<li>" + i + "</li>")) + "</ul>" : string.Join("\n", items.Select(i => "- " + i)) + "\n\n";
    private static string Generics(IReadOnlyList<GenericParameterDocument> parameters, Func<string, string?> link, bool html)
    {
        if (parameters.Count == 0) return "";
        var b = new StringBuilder(html ? "<h3>Generic Parameters</h3>" : "### Generic Parameters\n\n");
        foreach (var parameter in parameters)
        {
            var name = (parameter.Variance == "None" ? "" : parameter.Variance.ToLowerInvariant() + " ") + parameter.Name;
            b.Append(html ? "<h4><code>" + E(name) + "</code></h4>" : "**" + Md(name) + "**\n\n");
            b.Append(List(parameter.ConstraintDetails.Count > 0
                ? parameter.ConstraintDetails.Select(c => string.Concat(c.Declaration.Select(p => Linked(p.Text, p.TargetId is null ? null : link(p.TargetId), html))))
                : parameter.ConstraintItems.Select(c => html ? E(c) : Md(c)), html));
            b.Append(Attributes(parameter.Attributes, link, html));
        }
        return b.ToString();
    }
    private static string MemberMetadata(MemberDocument member, Func<string, string?> link, bool html)
    {
        string Heading(string name) => html ? "<h4>" + E(name) + "</h4>" : "**" + Md(name) + "**\n\n";
        var b = new StringBuilder(Attributes(member.Attributes, link, html));
        foreach (var parameter in member.Parameters.Where(p => p.Attributes.Count > 0))
            b.Append(Heading("Attributes on parameter " + parameter.Name) + Attributes(parameter.Attributes, link, html, false));
        foreach (var accessor in member.AccessorDetails)
        {
            if (accessor.Attributes.Count > 0) b.Append(Heading("Attributes on " + accessor.Kind) + Attributes(accessor.Attributes, link, html, false));
            foreach (var parameter in accessor.Parameters.Where(p => p.Attributes.Count > 0))
                b.Append(Heading("Attributes on " + accessor.Kind + " parameter " + parameter.Name) + Attributes(parameter.Attributes, link, html, false));
        }
        return b.ToString();
    }
    private static string Attributes(IReadOnlyList<AttributeDocument> attributes, Func<string, string?> link, bool html, bool heading = true)
    {
        if (attributes.Count == 0) return "";
        var title = heading ? html ? "<h3>Attributes</h3>" : "### Attributes\n\n" : "";
        return title + List(attributes.Select(a => (a.Target is "return" or "module" ? a.Target + ": " : "") + Linked(a.Display, a.Type?.TargetId is { } id ? link(id) : null, html)), html);
    }
    private static string Sources(SymbolDocument symbol, bool html)
    {
        if (symbol.Declarations.Count == 0) return "";
        return (html ? "<details><summary>Declared in</summary>" : "**Declared in**\n\n") +
            List(symbol.Declarations.Select(s => (html ? "<code>" + E(s.File) + ":" + s.Line + "</code>" : "`" + s.File + ":" + s.Line + "`") + (s.Generated ? " (generated)" : "")), html) + (html ? "</details>" : "");
    }
    private static void RenderMarkdown(SolutionDocument doc, BuildOptions options, string directory)
    {
        var links = Links(doc, false, options.SingleFile);
        var b = new StringBuilder("# " + Md(doc.Name) + " API\n\n" + Warning(doc, false));
        b.Append("Input: `" + doc.Input + "`\n\n## Projects\n\n");
        b.Append(List(doc.Projects.Select(p => Md(p.Name) + " (`" + p.File + "`, " + Md(p.TargetFramework ?? "unknown framework") + ")"), false));
        b.Append("## Namespaces\n\n");
        b.Append(List(doc.Namespaces.Select(ns => options.SingleFile ? Md(NamespaceName(ns.Name)) : Linked(NamespaceName(ns.Name), NamespaceFile(ns.Name, false), false)), false));
        b.Append("## Types\n\n" + TypeTable(doc.Types, id => Link(links, id), false));
        foreach (var project in doc.Projects.Where(p => p.Attributes.Count > 0))
            b.Append("## Project metadata: " + Md(project.Name) + "\n\n" + Attributes(project.Attributes, id => Link(links, id), false));
        if (options.SingleFile)
        {
            foreach (var type in doc.Types)
                b.Append("---\n\n" + TypeContent(doc, type, options, id => Link(links, id), false));
            Write(directory, "api.md", b.ToString());
        }
        else
        {
            Write(directory, "README.md", b.ToString());
            foreach (var ns in doc.Namespaces)
            {
                b = new("# " + Md(NamespaceName(ns.Name)) + "\n\n" + Warning(doc, false) + "[API index](../README.md)\n\n");
                foreach (var group in doc.Types.Where(t => t.Namespace == ns.Name).GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                    b.Append("## " + group.Key + "\n\n" + TypeTable(group, id => Link(links, id, "../"), false));
                Write(directory, NamespaceFile(ns.Name, false), b.ToString());
            }
            foreach (var type in doc.Types)
                Write(directory, "types/" + type.Anchor + ".md", "[API index](../README.md)\n\n" + TypeContent(doc, type, options, id => Link(links, id, "../"), false));
        }
    }
    private const string Css = """
        :root{color-scheme:light dark;--bg:#fff;--ink:#203047;--accent:#1559a7;--line:#d7e0eb;--soft:#f3f6fa;--muted:#53657c;font:16px/1.6 system-ui,sans-serif}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink)}a{color:var(--accent);text-underline-offset:3px}a:hover{text-decoration-thickness:2px}header{background:#19354e;color:white;padding:1rem 3vw;display:flex;align-items:center;gap:2rem;flex-wrap:wrap}header a{color:white}.brand{font-weight:750;text-decoration:none;font-size:1.2rem}nav{display:flex;gap:1.3rem}main{max-width:1200px;margin:2rem auto;padding:0 2rem}h1{font-size:2rem;overflow-wrap:anywhere}h2{margin-top:2.4rem;padding-bottom:.3rem;border-bottom:2px solid var(--line)}h3{margin-top:1.8rem}code{font:0.9em/1.6 ui-monospace,Consolas,monospace;overflow-wrap:anywhere}pre{padding:1.2rem;background:var(--soft);border:1px solid var(--line);border-radius:6px;overflow:auto;white-space:pre-wrap;tab-size:4}pre a{text-decoration:none}pre a:hover{text-decoration:underline}table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{padding:.8rem 1rem;text-align:left;vertical-align:top;border:1px solid var(--line);overflow-wrap:anywhere}thead{background:var(--soft)}tbody th{font-weight:500;width:55%}.muted{color:var(--muted)}article{padding:0 0 1.5rem;border-bottom:1px solid var(--line);scroll-margin-top:1rem}details{color:var(--muted);margin:1rem 0}summary{cursor:pointer}.warning{background:#fff1cb;color:#553900;border-left:5px solid #d79900;padding:1rem;margin-bottom:1rem}.search{background:var(--soft);padding:1rem 3vw}.search label{font-weight:650;margin-right:1rem}input{font:inherit;padding:.5rem .8rem;border:1px solid var(--line);border-radius:4px;width:min(100%,600px);background:var(--bg);color:var(--ink)}#search-results{list-style:none;padding:0;margin:0;max-height:25rem;overflow:auto}#search-results li{padding:.6rem 0;border-bottom:1px solid var(--line)}#search-results small{display:block;color:var(--muted)}footer{padding:2rem;text-align:center;color:var(--muted);font-size:.85rem}
        @media(prefers-color-scheme:dark){:root{--bg:#17212c;--ink:#e1e9f1;--accent:#8fc6ff;--line:#384b60;--soft:#1e2c3b;--muted:#a8b9cb}}
        @media(max-width:640px){main{padding:0 1rem}header{padding:1rem}nav{gap:1rem;font-size:.9rem}th,td{padding:.5rem}.search label{display:block}h1{font-size:1.5rem}}
        """;
    private const string SearchScript = """
        (()=>{'use strict';const root=document.currentScript.dataset.root;const input=document.getElementById('search');const results=document.getElementById('search-results');const status=document.getElementById('search-status');const entries=window.CODEDOCS_SEARCH||[];input.addEventListener('input',()=>{const terms=input.value.trim().toLowerCase().split(/\s+/).filter(Boolean);results.replaceChildren();if(!terms.length){status.textContent='';return;}const found=entries.filter(e=>terms.every(t=>(e.name+' '+e.scope+' '+e.summary).toLowerCase().includes(t)));status.textContent=found.length+' results'+(found.length>100?' (showing first 100)':'');for(const e of found.slice(0,100)){const li=document.createElement('li');const a=document.createElement('a');a.href=root+e.url;a.textContent=e.name;const small=document.createElement('small');small.textContent=e.scope+(e.summary?' — '+e.summary:'');li.append(a,small);results.append(li);}});})();
        """;
}
