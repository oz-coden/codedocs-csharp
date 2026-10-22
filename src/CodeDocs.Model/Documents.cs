using System.Security.Cryptography;
using System.Text;

namespace CodeDocs.Model;

public enum AnalysisState { Success, Partial, Fatal }
public enum Visibility { Public, Api, NonPrivate, All }
public enum IncompletePolicy { KeepExisting, Overwrite, Fail }

public sealed record TextPart(string Text, string? TargetId = null);
public sealed record TypeReference(string Display, string? TargetId, string Kind,
    IReadOnlyList<TypeReference> Arguments, string NullableAnnotation = "None")
{
    public string Name { get; init; } = "";
    public string MetadataName { get; init; } = "";
    public string Namespace { get; init; } = "";
    public string? AssemblyIdentity { get; init; }
    public bool IsValueType { get; init; }
    public bool IsReferenceType { get; init; }
    public bool IsNativeInteger { get; init; }
    public bool IsUnboundGenericType { get; init; }
    public TypeReference? ContainingType { get; init; }
    public IReadOnlyList<TupleElementDocument> TupleElements { get; init; } = [];
    public int? ArrayRank { get; init; }
    public bool? IsSZArray { get; init; }
    public TypeReference? ElementType { get; init; }
    public TypeReference? PointedAtType { get; init; }
    public FunctionPointerDocument? FunctionPointer { get; init; }
    public TypeParameterReference? TypeParameter { get; init; }
}
public sealed record TupleElementDocument(string Name, bool IsExplicitlyNamed, TypeReference Type);
public sealed record TypeParameterReference(int Ordinal, string Kind, string? DeclaringDocumentationId);
public sealed record FunctionPointerParameterDocument(TypeReference Type, string RefKind, string ScopedKind);
public sealed record FunctionPointerDocument(TypeReference ReturnType, string ReturnRefKind, string CallingConvention,
    IReadOnlyList<TypeReference> UnmanagedCallingConventionTypes, IReadOnlyList<FunctionPointerParameterDocument> Parameters);
public sealed record ConstantDocument(string Kind, TypeReference? Type, string Display)
{
    public bool IsNull { get; init; }
    public object? Value { get; init; }
    public TypeReference? ReferencedType { get; init; }
    public IReadOnlyList<ConstantDocument> Elements { get; init; } = [];
}
public sealed record GenericConstraintDocument(string Kind, string Display, TypeReference? Type, IReadOnlyList<TextPart> Declaration);
public sealed record DocumentationNode(string Kind, string? Name, string? Text,
    IReadOnlyDictionary<string, string> Attributes, IReadOnlyList<DocumentationNode> Children, string? ReferenceId = null);
public sealed record DocumentationInheritance(string? TargetId, string? DocumentationId, string? Path, string Status);
public sealed record SourceDeclaration(string File, int Line, string Declaration, bool Generated);
public sealed record AttributeDocument(string Display, TypeReference? Type, string Target,
    IReadOnlyList<string> ConstructorArguments, IReadOnlyDictionary<string, string> NamedArguments)
{
    public string? ConstructorId { get; init; }
    public IReadOnlyList<ConstantDocument> ConstructorValues { get; init; } = [];
    public IReadOnlyDictionary<string, ConstantDocument> NamedValues { get; init; } = new SortedDictionary<string, ConstantDocument>(StringComparer.Ordinal);
}
public sealed record GenericParameterDocument(string Name, string Variance, IReadOnlyList<TextPart> Constraints,
    IReadOnlyList<string> ConstraintItems, IReadOnlyList<AttributeDocument> Attributes)
{
    public IReadOnlyList<GenericConstraintDocument> ConstraintDetails { get; init; } = [];
}
public sealed record AccessorDocument(string Kind, string Accessibility, IReadOnlyList<AttributeDocument> Attributes, IReadOnlyList<string> Modifiers)
{
    public IReadOnlyList<ParameterDocument> Parameters { get; init; } = [];
}
public sealed record ParameterDocument(string Name, TypeReference Type, string RefKind, string ScopedKind,
    bool IsParams, bool IsOptional, string? DefaultValue, IReadOnlyList<AttributeDocument> Attributes)
{
    public ConstantDocument? Default { get; init; }
}
public sealed record Relationship(string Kind, TypeReference Type, string? MemberId = null);
public sealed record DocumentationComment(string RawXml, string Xml, IReadOnlyDictionary<string, string> References,
    string? InheritedFrom = null)
{
    public IReadOnlyList<DocumentationNode> Sections { get; init; } = [];
    public IReadOnlyList<DocumentationInheritance> Inheritances { get; init; } = [];
}
public sealed record DiagnosticDocument(string Severity, string Code, string Message, string? File = null, int? Line = null);
public sealed record InheritedMemberDocument(string DeclaringType, string? TypeId, string Name, string? MemberId, string Category);

public abstract record SymbolDocument
{
    public required string Id { get; init; }
    public required string DocumentationId { get; init; }
    public required string Name { get; init; }
    public required string Accessibility { get; init; }
    public required IReadOnlyList<TextPart> Declaration { get; init; }
    public required IReadOnlyList<string> Modifiers { get; init; }
    public required IReadOnlyList<SourceDeclaration> Declarations { get; init; }
    public required IReadOnlyList<AttributeDocument> Attributes { get; init; }
    public required DocumentationComment Documentation { get; init; }
    public required IReadOnlyList<GenericParameterDocument> GenericParameters { get; init; }
    public required IReadOnlyList<Relationship> Relationships { get; init; }
    public bool IsImplicitlyDeclared { get; init; }
    public string Anchor => StableId.Anchor(Id);
}

public sealed record MemberDocument : SymbolDocument
{
    public required string Category { get; init; }
    public TypeReference? ValueType { get; init; }
    public IReadOnlyList<ParameterDocument> Parameters { get; init; } = [];
    public IReadOnlyList<string> ExplicitImplementations { get; init; } = [];
    public IReadOnlyList<string> Accessors { get; init; } = [];
    public IReadOnlyList<AccessorDocument> AccessorDetails { get; init; } = [];
    public string? ConstantValue { get; init; }
    public ConstantDocument? Constant { get; init; }
    public string? ReturnRefKind { get; init; }
    public bool IsExtensionMethod { get; init; }
    public TypeReference? ExtensionTarget { get; init; }
    public bool IsPrimaryConstructor { get; init; }
}

public sealed record TypeDocument : SymbolDocument
{
    public required string Namespace { get; init; }
    public required string ProjectId { get; init; }
    public required string Kind { get; init; }
    public string? ContainingTypeId { get; init; }
    public TypeReference? BaseType { get; init; }
    public IReadOnlyList<TypeReference> Interfaces { get; init; } = [];
    public IReadOnlyList<MemberDocument> Members { get; init; } = [];
    public IReadOnlyList<InheritedMemberDocument> InheritedMembers { get; init; } = [];
    public MemberDocument? DelegateSignature { get; init; }
}

public sealed record NamespaceDocument(string Name, IReadOnlyList<string> TypeIds);
public sealed record ProjectDocument(string Id, string Name, string File, string AssemblyName,
    string? TargetFramework, string LanguageVersion, string NullableContext,
    IReadOnlyList<string> PreprocessorSymbols, IReadOnlyList<string> References,
    IReadOnlyList<AttributeDocument> Attributes, IReadOnlyList<string> TypeIds);
public sealed record SolutionDocument(string Name, string Input, string ToolVersion, AnalysisState State,
    IReadOnlyList<ProjectDocument> Projects, IReadOnlyList<NamespaceDocument> Namespaces,
    IReadOnlyList<TypeDocument> Types, IReadOnlyList<DiagnosticDocument> Diagnostics);

public static class StableId
{
    public static string Anchor(string id) => "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
    public static string TypeFile(string id) => Anchor(id) + ".html";
}
