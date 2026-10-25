using System.Text;
using System.Text.RegularExpressions;
using CodeDocs.Model;
using static CodeDocs.Rendering.DocumentationContent;
using static CodeDocs.Rendering.RenderOutput;
using static CodeDocs.Rendering.MarkdownMarkup;

namespace CodeDocs.Rendering;

public static class MarkdownRenderer
{
    public static void Render(SolutionDocument doc, BuildOptions options, string directory)
    {
        var links = new SymbolLinks(doc, t => options.SingleFile ? "" : "types/" + t.Anchor + ".md");
        var b = new StringBuilder("# " + Md(doc.Name) + " API\n\n" + Warning(doc));
        b.Append("Input: `" + doc.Input + "`\n\n## Projects\n\n");
        b.Append(List(doc.Projects.Select(p => Md(p.Name) + " (`" + p.File + "`, " + Md(p.TargetFramework ?? "unknown framework") + ")")));
        b.Append("## Namespaces\n\n");
        b.Append(List(doc.Namespaces.Select(ns => options.SingleFile ? Md(NamespaceName(ns.Name)) : Linked(NamespaceName(ns.Name), NamespaceFile(ns.Name)))));
        b.Append("## Types\n\n" + TypeTable(doc.Types, id => links.Resolve(id)));
        foreach (var project in doc.Projects.Where(p => p.Attributes.Count > 0))
            b.Append("## Project metadata: " + Md(project.Name) + "\n\n" + Attributes(project.Attributes, id => links.Resolve(id)));
        if (options.SingleFile)
        {
            foreach (var type in doc.Types)
                b.Append("---\n\n" + TypeContent(doc, type, options, id => links.Resolve(id)));
            Write(directory, "api.md", b.ToString());
        }
        else
        {
            Write(directory, "README.md", b.ToString());
            foreach (var ns in doc.Namespaces)
            {
                b = new("# " + Md(NamespaceName(ns.Name)) + "\n\n" + Warning(doc) + "[API index](../README.md)\n\n");
                foreach (var group in doc.Types.Where(t => t.Namespace == ns.Name).GroupBy(t => t.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                    b.Append("## " + group.Key + "\n\n" + TypeTable(group, id => links.Resolve(id, "../")));
                Write(directory, NamespaceFile(ns.Name), b.ToString());
            }

            foreach (var type in doc.Types)
                Write(directory, "types/" + type.Anchor + ".md", "[API index](../README.md)\n\n" + TypeContent(doc, type, options, id => links.Resolve(id, "../")));
        }
    }

    private static string Linked(string label, string? target) => target is null ? Md(label) : "[" + Md(label) + "](" + target + ")";
    private static string Declaration(SymbolDocument symbol, Func<string, string?> link) => Fence(string.Concat(symbol.Declaration.Select(p => p.Text)), "csharp") + "\n\n" + string.Join(" · ", symbol.Declaration.Where(p => p.TargetId is not null).Select(p => (p.Text, Id: p.TargetId!)).DistinctBy(p => p.Id).Where(p => link(p.Id) is not null).Select(p => Linked(p.Text, link(p.Id)))) + "\n\n";
    private static string NamespaceFile(string ns) => "namespaces/" + StableId.Anchor("N:" + ns) + ".md";
    private static string Warning(SolutionDocument doc) => doc.State == AnalysisState.Partial ? "> **Warning:** Generated from an incomplete compilation. Some API information may be missing or unresolved.\n\n" : "";
    private static string TypeTable(IEnumerable<TypeDocument> types, Func<string, string?> link)
    {
        var rows = Presentation.OrderTypes(types).Select(t => "- " + Linked(t.Name, link(t.Id)) + (Summary(t.Documentation) is { Length: > 0 } summary ? " — " + Md(summary) : ""));
        return string.Join("\n", rows) + "\n\n";
    }

    private static string TypeContent(SolutionDocument doc, TypeDocument type, BuildOptions options, Func<string, string?> link)
    {
        var b = new StringBuilder(Warning(doc));
        string Heading(string text, int level = 2) => new string('#', level) + " " + Md(text) + "\n\n";
        string Paragraph(string text) => text + "\n\n";
        b.Append("<a id=\"" + type.Anchor + "\"></a>\n\n# " + Md(type.Name) + "\n\n");
        b.Append(Paragraph(Md(NamespaceName(type.Namespace)) + " · " + Md(doc.Projects.First(p => p.Id == type.ProjectId).Name)));
        b.Append(Declaration(type, link));
        var docs = new MarkdownDocumentationRenderer(link);
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
        var docs = new MarkdownDocumentationRenderer(link);
        string Heading(string text, int level = 2) => new string('#', level) + " " + Md(text) + "\n\n";
        string Paragraph(string text) => text + "\n\n";
        var groups = Presentation.MemberGroups(type);
        if (groups.Count > 0)
            b.Append(Heading("Member Summary"));
        foreach (var(category, members)in groups)
        {
            b.Append(Heading(category, 3));
            foreach (var member in members)
            {
                var name = string.Concat(member.Declaration.Select(p => p.Text));
                var summary = Summary(member.Documentation);
                b.Append("- " + Linked(name, "#" + member.Anchor) + (summary.Length > 0 ? " — " + Md(summary) : "") + "\n");
            }

            b.Append("\n");
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
                b.Append("<a id=\"" + member.Anchor + "\"></a>\n\n### " + Md(member.Name) + "\n\n");
                b.Append(Declaration(member, link));
                if (member.IsPrimaryConstructor)
                    b.Append(Paragraph("Primary constructor."));
                b.Append(docs.Render(member.Documentation));
                b.Append(Generics(member.GenericParameters, link));
                b.Append(MemberMetadata(member, link));
                if (options.ShowDeclarationFiles)
                    b.Append(Sources(member));
            }
        }

        return b.ToString();
    }

    private static string List(IEnumerable<string> items) => string.Join("\n", items.Select(i => "- " + i)) + "\n\n";
    private static string Generics(IReadOnlyList<GenericParameterDocument> parameters, Func<string, string?> link)
    {
        if (parameters.Count == 0)
            return "";
        var b = new StringBuilder("### Generic Parameters\n\n");
        foreach (var parameter in parameters)
        {
            var name = (parameter.Variance == "None" ? "" : parameter.Variance.ToLowerInvariant() + " ") + parameter.Name;
            b.Append("**" + Md(name) + "**\n\n");
            b.Append(List(parameter.ConstraintDetails.Count > 0 ? parameter.ConstraintDetails.Select(c => string.Concat(c.Declaration.Select(p => Linked(p.Text, p.TargetId is null ? null : link(p.TargetId))))) : parameter.ConstraintItems.Select(c => Md(c))));
            b.Append(Attributes(parameter.Attributes, link));
        }

        return b.ToString();
    }

    private static string MemberMetadata(MemberDocument member, Func<string, string?> link)
    {
        string Heading(string name) => "**" + Md(name) + "**\n\n";
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
        var title = heading ? "### Attributes\n\n" : "";
        return title + List(attributes.Select(a => (a.Target is "return" or "module" or "field" ? a.Target + ": " : "") + Linked(a.Display, a.Type?.TargetId is { } id ? link(id) : null)));
    }

    private static string Sources(SymbolDocument symbol)
    {
        if (symbol.Declarations.Count == 0)
            return "";
        return "**Declared in**\n\n" + List(symbol.Declarations.Select(s => ("`" + s.File + ":" + s.Line + "`") + (s.Generated ? " (generated)" : "")));
    }
}

internal static class MarkdownMarkup
{
    internal static string Md(string text) => text.Replace("&", "&amp;").Replace("\\", "\\\\").Replace("`", "\\`").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "\\|");
    internal static string Fence(string text, string language = "")
    {
        var longest = Regex.Matches(text, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        return fence + language + "\n" + text + "\n" + fence;
    }
}
