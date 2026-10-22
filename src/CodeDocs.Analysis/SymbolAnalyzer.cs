using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using CodeDocs.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeDocs.Analysis;

public sealed class SymbolAnalyzer
{
    private readonly IReadOnlyList<CompilationUnit> units;
    private readonly string root;
    private readonly Visibility visibility;
    private readonly Dictionary<string, List<(string Project, string Id)>> ids = new(StringComparer.Ordinal);
    private readonly List<DiagnosticDocument> diagnostics = [];
    private CompilationUnit current = null!;
    private string projectId = "";
    private static readonly SymbolDisplayFormat Display = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface |
            SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
            SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly SymbolDisplayFormat TypeDisplay = Display.WithGenericsOptions(
        SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance);

    public SymbolAnalyzer(IReadOnlyList<CompilationUnit> units, string root, Visibility visibility)
    {
        this.units = units;
        this.root = root;
        this.visibility = visibility;
    }

    public SolutionDocument Analyze(string input, IEnumerable<DiagnosticDocument>? initialDiagnostics = null)
    {
        if (initialDiagnostics is not null) diagnostics.AddRange(initialDiagnostics);
        foreach (var unit in units)
        {
            current = unit; projectId = ProjectId(unit);
            foreach (var type in Types(unit.Compilation.Assembly.GlobalNamespace))
            {
                if (!Visible(type)) continue;
                Register(type);
                foreach (var member in ApiMembers(type).Where(Visible)) Register(member);
            }
        }
        var projects = new List<ProjectDocument>();
        var types = new List<TypeDocument>();
        foreach (var unit in units)
        {
            current = unit; projectId = ProjectId(unit);
            foreach (var diagnostic in unit.Compilation.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
            {
                var span = diagnostic.Location.GetLineSpan();
                diagnostics.Add(new(diagnostic.Severity.ToString(), diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture),
                    diagnostic.Location.IsInSource ? Relative(span.Path) : null,
                    diagnostic.Location.IsInSource ? span.StartLinePosition.Line + 1 : null));
            }
            var projectTypes = Types(unit.Compilation.Assembly.GlobalNamespace).Where(Visible).Select(BuildType).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            types.AddRange(projectTypes);
            var parse = unit.Compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions;
            projects.Add(new(projectId, unit.Name, unit.File, unit.Compilation.AssemblyName ?? unit.Name,
                TargetFramework(unit.Compilation), parse?.LanguageVersion.ToString() ?? "Default",
                (unit.Compilation.Options as CSharpCompilationOptions)?.NullableContextOptions.ToString() ?? "Unknown",
                parse?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray() ?? [],
                unit.Compilation.References.Select(r => r.Display is null ? "<compilation>" : Path.GetFileName(r.Display)).Order(StringComparer.Ordinal).ToArray(),
                Attributes(unit.Compilation.Assembly), projectTypes.Select(t => t.Id).ToArray()));
        }
        var orderedTypes = types.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        var orderedDiagnostics = diagnostics.Select(d => d with { Message = d.Message.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.Ordinal) }).Distinct().OrderBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToArray();
        return new(Path.GetFileNameWithoutExtension(input), Path.GetFileName(input), "0.1.0",
            orderedDiagnostics.Any(d => d.Severity == "Error") ? AnalysisState.Partial : AnalysisState.Success,
            projects.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
            orderedTypes.GroupBy(t => t.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new NamespaceDocument(g.Key, g.Select(t => t.Id).ToArray())).ToArray(),
            orderedTypes, orderedDiagnostics);
    }

    private static string? TargetFramework(Compilation compilation) => compilation.Assembly.GetAttributes()
        .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.Runtime.Versioning.TargetFrameworkAttribute")?.ConstructorArguments.FirstOrDefault().Value as string;
    private static string ProjectId(CompilationUnit unit) => unit.File + "|" + unit.Name;
    private static string Key(ISymbol symbol) => symbol.ContainingAssembly?.Identity.Name + "|" + symbol.OriginalDefinition.GetDocumentationCommentId();
    private string Id(ISymbol symbol) => projectId + "::" + (symbol.GetDocumentationCommentId() ?? symbol.Kind + ":" + symbol.ToDisplayString());
    private void Register(ISymbol symbol)
    {
        var key = Key(symbol);
        if (!ids.TryGetValue(key, out var values)) ids[key] = values = [];
        values.Add((projectId, Id(symbol)));
    }
    private string? Resolve(ISymbol? symbol)
    {
        if (symbol is null) return null;
        if (symbol is IMethodSymbol { PartialDefinitionPart: { } definition }) symbol = definition;
        if (!ids.TryGetValue(Key(symbol), out var values)) return null;
        return values.FirstOrDefault(v => v.Project == projectId).Id ?? values.OrderBy(v => v.Project, StringComparer.Ordinal).First().Id;
    }
    private string Relative(string path) => string.IsNullOrEmpty(path) ? "<generated>" : (Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path).Replace('\\', '/');
    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
    {
        foreach (var child in ns.GetNamespaceMembers().OrderBy(n => n.Name, StringComparer.Ordinal))
            foreach (var type in Types(child)) yield return type;
        foreach (var type in ns.GetTypeMembers().OrderBy(t => t.MetadataName, StringComparer.Ordinal))
            foreach (var nested in SelfAndNested(type)) yield return nested;
    }
    private static IEnumerable<INamedTypeSymbol> SelfAndNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers().OrderBy(t => t.MetadataName, StringComparer.Ordinal))
            foreach (var child in SelfAndNested(nested)) yield return child;
    }
    private static IEnumerable<ISymbol> ApiMembers(INamedTypeSymbol type) => type.GetMembers().Where(m => m switch
    {
        INamedTypeSymbol => false,
        IMethodSymbol method => method.PartialDefinitionPart is null && method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.ExplicitInterfaceImplementation,
        IFieldSymbol field => field.AssociatedSymbol is null && field.Name != "value__",
        IPropertySymbol => true,
        IEventSymbol => true,
        _ => false
    });

    private bool Visible(ISymbol symbol)
    {
        if (visibility == Visibility.All) return true;
        for (var container = symbol.ContainingType; container is not null; container = container.ContainingType)
            if (!VisibleAccessibility(container.DeclaredAccessibility)) return false;
        if (VisibleAccessibility(symbol.DeclaredAccessibility)) return true;
        return ExplicitSymbols(symbol).Any(s => Visible(s.ContainingType));
    }
    private bool VisibleAccessibility(Accessibility accessibility) => visibility switch
    {
        Visibility.Public => accessibility == Accessibility.Public,
        Visibility.Api => accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal,
        Visibility.NonPrivate => accessibility is not (Accessibility.Private or Accessibility.NotApplicable),
        _ => true
    };
    private static IEnumerable<ISymbol> ExplicitSymbols(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.ExplicitInterfaceImplementations,
        IPropertySymbol p => p.ExplicitInterfaceImplementations,
        IEventSymbol e => e.ExplicitInterfaceImplementations,
        _ => []
    };
    private static string AccessibilityText(ISymbol symbol) => symbol is INamedTypeSymbol { IsFileLocal: true } ? "file" : symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public", Accessibility.Private => "private", Accessibility.Internal => "internal",
        Accessibility.Protected => "protected", Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected", _ => ""
    };

    private IReadOnlyList<TextPart> Parts(ISymbol symbol, bool typeOnly = false) => symbol.ToDisplayParts(typeOnly ? TypeDisplay : Display)
        .Select(p => new TextPart(p.ToString(), Resolve(p.Symbol))).ToArray();
    private TypeReference TypeRef(ITypeSymbol symbol) => new(symbol.ToDisplayString(TypeDisplay), Resolve(symbol), symbol.TypeKind.ToString(),
        symbol switch
        {
            INamedTypeSymbol n => n.TypeArguments.Select(TypeRef).ToArray(),
            IArrayTypeSymbol a => [TypeRef(a.ElementType)],
            IPointerTypeSymbol p => [TypeRef(p.PointedAtType)],
            IFunctionPointerTypeSymbol f => [TypeRef(f.Signature.ReturnType), .. f.Signature.Parameters.Select(p => TypeRef(p.Type))],
            _ => []
        }, symbol.NullableAnnotation.ToString());

    private static string Kind(INamedTypeSymbol type) => type.IsRecord
        ? type.TypeKind == TypeKind.Struct ? "Record structs" : "Records"
        : type.TypeKind switch { TypeKind.Class => "Classes", TypeKind.Struct => "Structs", TypeKind.Interface => "Interfaces", TypeKind.Enum => "Enums", TypeKind.Delegate => "Delegates", _ => "Types" };
    private static string Category(ISymbol symbol) => ExplicitSymbols(symbol).Any() ? "Explicit Interface Implementations" : symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "Constructors",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "Operators",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "Conversions",
        IMethodSymbol => "Methods",
        IPropertySymbol { IsIndexer: true } => "Indexers",
        IPropertySymbol => "Properties", IEventSymbol => "Events",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "Enum Members",
        _ => "Fields"
    };

    private TypeDocument BuildType(INamedTypeSymbol type)
    {
        var members = ApiMembers(type).Where(Visible).Select(BuildMember).OrderBy(m => m.Id, StringComparer.Ordinal).ToArray();
        var relationships = new List<Relationship>();
        if (type.BaseType is not null) relationships.Add(new("BaseType", TypeRef(type.BaseType)));
        relationships.AddRange(type.Interfaces.Select(t => new Relationship("Interface", TypeRef(t))));
        if (type.DelegateInvokeMethod is { } invoke)
        {
            relationships.Add(new("ReturnType", TypeRef(invoke.ReturnType)));
            relationships.AddRange(invoke.Parameters.Select(p => new Relationship("Parameter", TypeRef(p.Type))));
        }
        relationships.AddRange(type.TypeParameters.SelectMany(p => p.ConstraintTypes).Select(t => new Relationship("GenericConstraint", TypeRef(t))));
        relationships.AddRange(type.GetAttributes().Where(a => a.AttributeClass is not null).Select(a => new Relationship("Attribute", TypeRef(a.AttributeClass!))));
        return new()
        {
            Id = Id(type), DocumentationId = type.GetDocumentationCommentId() ?? "", Name = type.ToDisplayString(TypeDisplay),
            Namespace = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString(), ProjectId = projectId,
            Accessibility = AccessibilityText(type), Declaration = TypeDeclaration(type), Kind = Kind(type),
            Modifiers = Modifiers(type), Declarations = Sources(type), Attributes = Attributes(type), Documentation = Documentation(type),
            GenericParameters = Generics(type.TypeParameters), Relationships = relationships,
            IsImplicitlyDeclared = type.IsImplicitlyDeclared, ContainingTypeId = Resolve(type.ContainingType),
            BaseType = type.BaseType is null ? null : TypeRef(type.BaseType), Interfaces = type.Interfaces.Select(TypeRef).ToArray(),
            Members = members, InheritedMembers = Inherited(type),
            DelegateSignature = type.DelegateInvokeMethod is null ? null : BuildMember(type.DelegateInvokeMethod)
        };
    }

    private IReadOnlyList<TextPart> TypeDeclaration(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Delegate) return type.ToDisplayParts(new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes, genericsOptions: Display.GenericsOptions, memberOptions: Display.MemberOptions, parameterOptions: Display.ParameterOptions, delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature, kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword, miscellaneousOptions: Display.MiscellaneousOptions)).Select(p => new TextPart(p.ToString(), Resolve(p.Symbol))).ToArray();
        var parts = new List<TextPart>();
        var modifiers = Modifiers(type).Where(m => m is not ("public" or "private" or "internal" or "protected" or "file"));
        var prefix = string.Join(" ", new[] { AccessibilityText(type) }.Concat(modifiers).Where(m => m.Length > 0));
        var keyword = type.IsRecord ? (type.TypeKind == TypeKind.Struct ? "record struct" : "record") : type.TypeKind.ToString().ToLowerInvariant();
        parts.Add(new(prefix + (prefix.Length > 0 ? " " : "") + keyword + " "));
        parts.Add(new(Escape(type.Name), Resolve(type)));
        if (type.TypeParameters.Length > 0)
        {
            parts.Add(new("<"));
            for (var i = 0; i < type.TypeParameters.Length; i++)
            {
                if (i > 0) parts.Add(new(", "));
                var parameter = type.TypeParameters[i];
                parts.Add(new((parameter.Variance == VarianceKind.None ? "" : parameter.Variance.ToString().ToLowerInvariant() + " ") + Escape(parameter.Name)));
            }
            parts.Add(new(">"));
        }
        var primary = type.InstanceConstructors.FirstOrDefault(IsPrimary);
        if (primary is not null)
        {
            parts.Add(new("("));
            for (var i = 0; i < primary.Parameters.Length; i++)
            {
                if (i > 0) parts.Add(new(", "));
                parts.AddRange(Parts(primary.Parameters[i]));
            }
            parts.Add(new(")"));
        }
        var bases = new List<ITypeSymbol>();
        if (type.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum) } baseType) bases.Add(baseType);
        if (type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType is { SpecialType: not SpecialType.System_Int32 } underlying) bases.Add(underlying);
        bases.AddRange(type.Interfaces);
        if (bases.Count > 0)
        {
            parts.Add(new(" : "));
            for (var i = 0; i < bases.Count; i++) { if (i > 0) parts.Add(new(", ")); parts.AddRange(Parts(bases[i], true)); }
        }
        foreach (var generic in Generics(type.TypeParameters).Where(g => g.ConstraintItems.Count > 0))
        {
            parts.Add(new("\n    where " + generic.Name + " : "));
            parts.AddRange(generic.Constraints);
        }
        return parts;
    }

    private MemberDocument BuildMember(ISymbol symbol)
    {
        var method = symbol as IMethodSymbol;
        var property = symbol as IPropertySymbol;
        var parameters = method?.Parameters ?? property?.Parameters ?? [];
        var valueType = symbol switch { IMethodSymbol m => m.ReturnType, IPropertySymbol p => p.Type, IFieldSymbol f => f.Type, IEventSymbol e => e.Type, _ => null };
        var relationships = parameters.Select(p => new Relationship(method?.MethodKind == MethodKind.Constructor ? "ConstructorParameter" : "Parameter", TypeRef(p.Type), Id(symbol))).ToList();
        if (valueType is not null) relationships.Add(new(symbol is IMethodSymbol ? "ReturnType" : symbol.Kind + "Type", TypeRef(valueType), Id(symbol)));
        relationships.AddRange((method?.TypeParameters ?? []).SelectMany(p => p.ConstraintTypes).Select(t => new Relationship("GenericConstraint", TypeRef(t), Id(symbol))));
        relationships.AddRange(symbol.GetAttributes().Where(a => a.AttributeClass is not null).Select(a => new Relationship("Attribute", TypeRef(a.AttributeClass!), Id(symbol))));
        var declaration = Parts(symbol).ToList();
        var modifiers = Modifiers(symbol);
        string[] prefixModifiers = ["public", "protected", "internal", "private", "file", "new", "static", "abstract", "virtual", "sealed", "override", "readonly", "extern", "required", "volatile", "const"];
        var insertion = 0;
        while (insertion < declaration.Count && (string.IsNullOrWhiteSpace(declaration[insertion].Text) || prefixModifiers.Contains(declaration[insertion].Text))) insertion++;
        foreach (var modifier in modifiers.Where(m => m is "async" or "unsafe" or "partial" or "scoped"))
            if (!declaration.Any(p => p.Text == modifier)) declaration.Insert(insertion++, new(modifier + " "));
        if (property is not null)
        {
            var text = string.Concat(declaration.Select(p => p.Text));
            var start = text.IndexOf(" {", StringComparison.Ordinal);
            if (start >= 0)
            {
                var length = 0;
                declaration = declaration.TakeWhile(p => { var keep = length < start; length += p.Text.Length; return keep; }).ToList();
            }
            declaration.Add(new(" { " + string.Join(" ", Accessors(property)) + " }"));
        }
        return new()
        {
            Id = Id(symbol), DocumentationId = symbol.GetDocumentationCommentId() ?? "", Name = symbol.Name,
            Accessibility = AccessibilityText(symbol), Declaration = declaration, Category = Category(symbol), Modifiers = modifiers,
            Declarations = Sources(symbol), Attributes = Attributes(symbol), Documentation = Documentation(symbol),
            GenericParameters = Generics(method?.TypeParameters ?? []), Relationships = relationships, IsImplicitlyDeclared = symbol.IsImplicitlyDeclared,
            ValueType = valueType is null ? null : TypeRef(valueType), Parameters = parameters.Select(Parameter).ToArray(),
            ExplicitImplementations = ExplicitSymbols(symbol).Select(s => Resolve(s) ?? s.GetDocumentationCommentId() ?? s.ToDisplayString()).ToArray(),
            Accessors = property is null ? [] : Accessors(property),
            AccessorDetails = AccessorSymbols(symbol).Select(a => new AccessorDocument(a.MethodKind.ToString(), AccessibilityText(a), Attributes(a), Modifiers(a))).ToArray(),
            ConstantValue = symbol is IFieldSymbol { HasConstantValue: true } field ? Constant(field.ConstantValue) : null,
            ReturnRefKind = method?.RefKind.ToString() ?? property?.RefKind.ToString(),
            IsExtensionMethod = method?.IsExtensionMethod ?? false,
            ExtensionTarget = method is { IsExtensionMethod: true, Parameters.Length: > 0 } ? TypeRef(method.Parameters[0].Type) : null,
            IsPrimaryConstructor = method is not null && IsPrimary(method)
        };
    }

    private static IEnumerable<IMethodSymbol> AccessorSymbols(ISymbol symbol) => (symbol switch
    {
        IPropertySymbol p => new[] { p.GetMethod, p.SetMethod },
        IEventSymbol e => new[] { e.AddMethod, e.RemoveMethod, e.RaiseMethod },
        _ => Array.Empty<IMethodSymbol?>()
    }).OfType<IMethodSymbol>();
    private static IReadOnlyList<string> Accessors(IPropertySymbol property) => new[] { property.GetMethod, property.SetMethod }.Where(m => m is not null).Select(m =>
        (m!.DeclaredAccessibility != property.DeclaredAccessibility ? AccessibilityText(m) + " " : "") +
        (m.MethodKind == MethodKind.PropertyGet ? "get;" : m.IsInitOnly ? "init;" : "set;")).ToArray();
    private ParameterDocument Parameter(IParameterSymbol p) => new(p.Name, TypeRef(p.Type), p.RefKind.ToString(), p.ScopedKind.ToString(),
        p.IsParams, p.IsOptional, p.HasExplicitDefaultValue ? Constant(p.ExplicitDefaultValue) : null, Attributes(p));
    private static bool IsPrimary(IMethodSymbol method) => method.MethodKind == MethodKind.Constructor && method.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax { ParameterList: not null });
    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    private static string Constant(object? value) => value switch
    {
        null => "null", string s => SymbolDisplay.FormatLiteral(s, true), char c => SymbolDisplay.FormatLiteral(c, true),
        bool b => b ? "true" : "false", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "null"
    };

    private IReadOnlyList<string> Modifiers(ISymbol symbol)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var references = symbol.DeclaringSyntaxReferences.AsEnumerable();
        if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation }) references = references.Concat(implementation.DeclaringSyntaxReferences);
        foreach (var syntax in references.Select(r => r.GetSyntax()))
        {
            var tokens = syntax switch
            {
                MemberDeclarationSyntax m => m.Modifiers,
                VariableDeclaratorSyntax v when v.Parent?.Parent is MemberDeclarationSyntax m => m.Modifiers,
                _ => default
            };
            foreach (var token in tokens) set.Add(token.ValueText);
        }
        if (symbol.IsStatic) set.Add("static");
        if (symbol.IsAbstract && symbol is not INamedTypeSymbol { IsStatic: true } && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface }) set.Add("abstract");
        if (symbol.IsVirtual) set.Add("virtual");
        if (symbol.IsOverride) set.Add("override");
        if (symbol.IsSealed && symbol is not INamedTypeSymbol { IsStatic: true } && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate }) set.Add("sealed");
        if (symbol is INamedTypeSymbol { IsReadOnly: true }) set.Add("readonly");
        if (symbol is INamedTypeSymbol { IsRefLikeType: true }) set.Add("ref");
        if (symbol is IFieldSymbol { IsVolatile: true }) set.Add("volatile");
        if (symbol is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }) set.Add("required");
        string[] order = ["public", "protected", "internal", "private", "file", "new", "static", "abstract", "virtual", "sealed", "override", "readonly", "ref", "required", "volatile", "unsafe", "async", "extern", "partial", "scoped", "const"];
        return set.OrderBy(s => Array.IndexOf(order, s) is var index && index >= 0 ? index : int.MaxValue).ThenBy(s => s, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<GenericParameterDocument> Generics(ImmutableArray<ITypeParameterSymbol> parameters) => parameters.Select(p =>
    {
        var items = new List<IReadOnlyList<TextPart>>();
        if (p.HasUnmanagedTypeConstraint) items.Add([new("unmanaged")]);
        else if (p.HasValueTypeConstraint) items.Add([new("struct")]);
        else if (p.HasReferenceTypeConstraint) items.Add([new(p.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class")]);
        if (p.HasNotNullConstraint) items.Add([new("notnull")]);
        items.AddRange(p.ConstraintTypes.Select(t => Parts(t, true)));
        if (p.HasConstructorConstraint) items.Add([new("new()")]);
        if (p.AllowsRefLikeType) items.Add([new("allows ref struct")]);
        var parts = new List<TextPart>();
        foreach (var item in items) { if (parts.Count > 0) parts.Add(new(", ")); parts.AddRange(item); }
        return new GenericParameterDocument(p.Name, p.Variance.ToString(), parts,
            items.Select(i => string.Concat(i.Select(s => s.Text))).ToArray(), Attributes(p));
    }).ToArray();

    private IReadOnlyList<AttributeDocument> Attributes(ISymbol symbol)
    {
        var result = symbol.GetAttributes().Select(a => Attribute(a, "symbol")).ToList();
        if (symbol is IAssemblySymbol assembly) result.AddRange(assembly.Modules.SelectMany(m => m.GetAttributes()).Select(a => Attribute(a, "module")));
        if (symbol is IMethodSymbol method) result.AddRange(method.GetReturnTypeAttributes().Select(a => Attribute(a, "return")));
        return result.OrderBy(a => a.Target, StringComparer.Ordinal).ThenBy(a => a.Display, StringComparer.Ordinal).ToArray();
    }
    private AttributeDocument Attribute(AttributeData a, string target) => new(a.ToString() ?? "<unknown>", a.AttributeClass is null ? null : TypeRef(a.AttributeClass), target,
        a.ConstructorArguments.Select(TypedConstantText).ToArray(),
        new SortedDictionary<string, string>(a.NamedArguments.ToDictionary(p => p.Key, p => TypedConstantText(p.Value)), StringComparer.Ordinal));
    private static string TypedConstantText(TypedConstant constant) => constant.Kind == TypedConstantKind.Array
        ? constant.IsNull ? "null" : "[" + string.Join(", ", constant.Values.Select(TypedConstantText)) + "]"
        : constant.Kind == TypedConstantKind.Type ? "typeof(" + constant.Value + ")" : Constant(constant.Value);

    private IReadOnlyList<SourceDeclaration> Sources(ISymbol symbol)
    {
        var references = symbol.DeclaringSyntaxReferences.AsEnumerable();
        if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation }) references = references.Concat(implementation.DeclaringSyntaxReferences);
        return references.Select(r =>
        {
            var node = r.GetSyntax();
            var line = node.GetLocation().GetLineSpan();
            var file = Relative(node.SyntaxTree.FilePath);
            var prefix = node.SyntaxTree.GetText().ToString(new Microsoft.CodeAnalysis.Text.TextSpan(0, Math.Min(512, node.SyntaxTree.Length)));
            var header = node switch
            {
                BaseMethodDeclarationSyntax m => m.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithoutLeadingTrivia().NormalizeWhitespace().ToFullString(),
                TypeDeclarationSyntax t => t.WithMembers(default).WithOpenBraceToken(default).WithCloseBraceToken(default).WithoutLeadingTrivia().NormalizeWhitespace().ToFullString().Trim(),
                BasePropertyDeclarationSyntax p => p.WithAccessorList(null).WithoutLeadingTrivia().NormalizeWhitespace().ToFullString(),
                _ => node.WithoutLeadingTrivia().NormalizeWhitespace().ToFullString()
            };
            return new SourceDeclaration(file, line.StartLinePosition.Line + 1, header,
                file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || prefix.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase));
        }).OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ToArray();
    }

    private DocumentationComment Documentation(ISymbol symbol)
    {
        var raw = symbol.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: true) ?? "";
        if (raw.Length == 0) return new("", "", new SortedDictionary<string, string>());
        try
        {
            var xml = ParseXml(raw);
            string? inheritedFrom = null;
            ResolveInheritdoc(symbol, xml, new HashSet<ISymbol>(SymbolEqualityComparer.Default), ref inheritedFrom);
            var references = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var cref in xml.DescendantsAndSelf().Attributes("cref").Select(a => a.Value).Distinct())
            {
                var target = DocumentationCommentId.GetFirstSymbolForReferenceId(cref, current.Compilation)
                    ?? DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, current.Compilation);
                if (Resolve(target) is { } id) references[cref] = id;
            }
            return new(raw, xml.ToString(SaveOptions.DisableFormatting), references, inheritedFrom);
        }
        catch (System.Xml.XmlException e)
        {
            diagnostics.Add(new("Warning", "XMLDOC", $"{symbol.ToDisplayString()}: {e.Message}"));
            return new(raw, "", new SortedDictionary<string, string>());
        }
    }
    private static XElement ParseXml(string xml)
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new System.Xml.XmlReaderSettings
        { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private void ResolveInheritdoc(ISymbol symbol, XElement xml, HashSet<ISymbol> visited, ref string? inheritedFrom)
    {
        if (!visited.Add(symbol)) return;
        foreach (var inherit in xml.Descendants("inheritdoc").ToArray())
        {
            if (inherit.Attribute("path") is not null)
            {
                diagnostics.Add(new("Warning", "INHERITDOC_PATH", $"XPath inheritdoc is preserved but not expanded for {symbol.ToDisplayString()}."));
                continue;
            }
            var cref = inherit.Attribute("cref")?.Value;
            var target = cref is null ? InheritanceTarget(symbol) : DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, current.Compilation)
                ?? DocumentationCommentId.GetFirstSymbolForReferenceId(cref, current.Compilation);
            if (target is null || visited.Contains(target)) continue;
            var raw = target.GetDocumentationCommentXml(CultureInfo.InvariantCulture, expandIncludes: true);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var inherited = ParseXml(raw);
            ResolveInheritdoc(target, inherited, visited, ref inheritedFrom);
            inheritedFrom = Resolve(target) ?? target.GetDocumentationCommentId();
            if (inherit.Parent == xml)
            {
                foreach (var element in inherited.Elements())
                {
                    if (element.Name.LocalName == "inheritdoc") continue;
                    var key = element.Attribute("name")?.Value ?? element.Attribute("cref")?.Value;
                    if (!xml.Elements(element.Name).Any(e => (e.Attribute("name")?.Value ?? e.Attribute("cref")?.Value) == key))
                        inherit.AddBeforeSelf(new XElement(element));
                }
                inherit.Remove();
            }
            else
            {
                var matching = inherited.Elements(inherit.Parent!.Name).FirstOrDefault();
                if (matching is not null) inherit.ReplaceWith(matching.Nodes().Select(CloneNode));
            }
        }
        visited.Remove(symbol);
    }
    private static XNode CloneNode(XNode node) => node is XElement element ? new XElement(element) : new XText(node.ToString());
    private ISymbol? InheritanceTarget(ISymbol symbol)
    {
        var overridden = symbol switch
        {
            IMethodSymbol m => (ISymbol?)m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            INamedTypeSymbol t => t.BaseType,
            _ => null
        };
        if (overridden is not null) return overridden;
        var explicitTarget = ExplicitSymbols(symbol).FirstOrDefault();
        if (explicitTarget is not null) return explicitTarget;
        return symbol.ContainingType?.AllInterfaces.SelectMany(i => i.GetMembers()).FirstOrDefault(member =>
            SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol));
    }

    private IReadOnlyList<InheritedMemberDocument> Inherited(INamedTypeSymbol type)
    {
        var result = new List<InheritedMemberDocument>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        string Signature(ISymbol s) => s switch
        {
            IMethodSymbol m => m.MetadataName + "`" + m.Arity + "(" + string.Join(",", m.Parameters.Select(p => p.RefKind + ":" + p.Type.ToDisplayString(TypeDisplay))) + ")",
            IPropertySymbol p => p.MetadataName + "(" + string.Join(",", p.Parameters.Select(x => x.Type.ToDisplayString(TypeDisplay))) + ")",
            _ => s.MetadataName
        };
        foreach (var member in ApiMembers(type)) signatures.Add(Signature(member));
        var bases = new List<INamedTypeSymbol>();
        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType) bases.Add(parent);
        if (type.TypeKind == TypeKind.Interface) bases.AddRange(type.AllInterfaces);
        foreach (var parent in bases)
        {
            foreach (var member in ApiMembers(parent).OrderBy(m => m.MetadataName, StringComparer.Ordinal))
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }) continue;
                if (!current.Compilation.IsSymbolAccessibleWithin(member, type) || member.DeclaredAccessibility == Accessibility.Private || !VisibleAccessibility(member.DeclaredAccessibility)) continue;
                if (!signatures.Add(Signature(member))) continue;
                result.Add(new(parent.ToDisplayString(TypeDisplay), Resolve(parent), member.ToDisplayString(Display), Resolve(member), Category(member)));
            }
        }
        return result;
    }
}
