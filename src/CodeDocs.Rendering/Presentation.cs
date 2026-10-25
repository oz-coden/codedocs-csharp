using CodeDocs.Model;

namespace CodeDocs.Rendering;

internal static class Presentation
{
    internal static readonly string[] Categories = ["Constructors", "Fields", "Enum Members", "Properties", "Indexers", "Events", "Methods", "Explicit Interface Implementations", "Operators", "Conversions"];

    internal static IEnumerable<TypeDocument> OrderTypes(IEnumerable<TypeDocument> types) =>
        types.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal);

    internal static IReadOnlyList<(string Category, MemberDocument[] Members)> MemberGroups(TypeDocument type)
    {
        var lookup = type.Members.ToLookup(m => m.Category, StringComparer.Ordinal);
        return Categories.Where(lookup.Contains).Select(c => (c, lookup[c].ToArray())).ToArray();
    }
}

// The renderers supply their own type paths; symbol identity and member ownership are format-independent.
internal sealed class SymbolLinks
{
    private readonly Dictionary<string, string> links = new(StringComparer.Ordinal);

    public SymbolLinks(SolutionDocument document, Func<TypeDocument, string> typePath)
    {
        foreach (var type in document.Types)
        {
            var path = typePath(type);
            links[type.Id] = path + "#" + type.Anchor;
            foreach (var member in type.Members) links[member.Id] = path + "#" + member.Anchor;
        }
    }

    public string this[string id] => links[id];
    public string? Resolve(string id, string prefix = "") => links.TryGetValue(id, out var url) ? prefix + url : null;
}
