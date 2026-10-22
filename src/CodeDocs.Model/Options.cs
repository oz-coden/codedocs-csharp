namespace CodeDocs.Model;

public sealed record BuildOptions
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public IReadOnlyList<string> Formats { get; init; } = ["html"];
    public Visibility Visibility { get; init; } = Visibility.Public;
    public IncompletePolicy OnIncomplete { get; init; } = IncompletePolicy.KeepExisting;
    public bool Search { get; init; } = true;
    public bool SingleFile { get; init; }
    public bool ShowDeclarationFiles { get; init; } = true;
    public bool Clean { get; init; }
    public bool Verbose { get; init; }
}
