using System.Globalization;
using CodeDocs.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeDocs.Analysis;
// Collects attribute targets and typed constants; renderers decide how to display them.
internal sealed class AttributeReader(Compilation compilation, Func<ITypeSymbol, TypeReference> typeRef, Func<ISymbol?, string?> resolve)
{
    private readonly Dictionary<ISymbol, IReadOnlyList<AttributeDocument>> cache = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, IFieldSymbol[]> fields = new(SymbolEqualityComparer.Default);
    public IReadOnlyList<AttributeDocument> Read(ISymbol symbol)
    {
        if (cache.TryGetValue(symbol, out var cached))
            return cached;
        var result = symbol.GetAttributes().Select(a => Attribute(a, "symbol")).ToList();
        if (symbol is IAssemblySymbol assembly)
            result.AddRange(assembly.Modules.SelectMany(m => m.GetAttributes()).Select(a => Attribute(a, "module")));
        if (symbol is IMethodSymbol method)
            result.AddRange(method.GetReturnTypeAttributes().Select(a => Attribute(a, "return")));
        if (symbol is IPropertySymbol or IEventSymbol)
        {
            if (!fields.TryGetValue(symbol.ContainingType, out var candidates))
                fields[symbol.ContainingType] = candidates = symbol.ContainingType.GetMembers().OfType<IFieldSymbol>().ToArray();
            var associated = candidates.Where(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, symbol)).ToArray();
            result.AddRange(associated.SelectMany(f => f.GetAttributes()).Select(a => Attribute(a, "field")));
            // Roslyn exposes auto-property fields, but not field-like event fields through GetMembers.
            if (symbol is IEventSymbol @event && associated.Length == 0)
                result.AddRange(EventFieldAttributes(@event));
        }

        return cache[symbol] = result.OrderBy(a => a.Target, StringComparer.Ordinal).ThenBy(a => a.Display, StringComparer.Ordinal).ToArray();
    }

    private IEnumerable<AttributeDocument> EventFieldAttributes(IEventSymbol symbol)
    {
        if (symbol.IsAbstract) yield break;
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax declaration })
                continue;
            if (declaration.Modifiers.Any(SyntaxKind.ExternKeyword)) continue;
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var syntax in declaration.AttributeLists.Where(l => l.Target?.Identifier.ValueText == "field").SelectMany(l => l.Attributes))
            {
                var creation = (model.GetOperation(syntax) as IAttributeOperation)?.Operation as IObjectCreationOperation;
                var constructor = creation?.Constructor ?? model.GetSymbolInfo(syntax).Symbol as IMethodSymbol;
                var attributeType = creation?.Type ?? constructor?.ContainingType ?? model.GetTypeInfo(syntax).Type;
                var arguments = creation?.Arguments.OrderBy(a => a.Parameter?.Ordinal).Select(a => OperationValue(a.Value)).ToArray() ?? [];
                var named = new SortedDictionary<string, ConstantDocument>(StringComparer.Ordinal);
                foreach (var assignment in creation?.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>() ?? [])
                {
                    var name = assignment.Target switch
                    {
                        IPropertyReferenceOperation p => p.Property.Name,
                        IFieldReferenceOperation f => f.Field.Name,
                        _ => null
                    };
                    if (name is not null)
                        named[name] = OperationValue(assignment.Value);
                }

                yield return new(syntax.WithoutTrivia().NormalizeWhitespace().ToString(), attributeType is null ? null : typeRef(attributeType), "field", arguments.Select(a => a.Display).ToArray(), new SortedDictionary<string, string>(named.ToDictionary(p => p.Key, p => p.Value.Display), StringComparer.Ordinal))
                {
                    ConstructorId = resolve(constructor),
                    ConstructorValues = arguments,
                    NamedValues = named
                };
            }
        }
    }

    private ConstantDocument OperationValue(IOperation operation)
    {
        if (operation is IConversionOperation conversion && !operation.ConstantValue.HasValue)
            return OperationValue(conversion.Operand);
        if (operation is ITypeOfOperation type)
            return new("Type", operation.Type is null ? null : typeRef(operation.Type), "typeof(" + type.TypeOperand + ")")
            {
                ReferencedType = typeRef(type.TypeOperand)
            };
        if (operation is IArrayCreationOperation array)
        {
            var elements = array.Initializer?.ElementValues.Select(OperationValue).ToArray() ?? [];
            return new("Array", operation.Type is null ? null : typeRef(operation.Type), "[" + string.Join(", ", elements.Select(e => e.Display)) + "]")
            {
                Elements = elements
            };
        }

        if (operation.ConstantValue.HasValue)
        {
            var value = Value(operation.Type, operation.ConstantValue.Value);
            return operation.Type is IArrayTypeSymbol ? value with { Kind = "Array" } : value;
        }

        // Incomplete compilations may contain an unbound attribute argument; retain its spelling.
        return new("Error", operation.Type is null ? null : typeRef(operation.Type), operation.Syntax.WithoutTrivia().ToString());
    }

    private AttributeDocument Attribute(AttributeData a, string target) => new(a.ToString() ?? "<unknown>", a.AttributeClass is null ? null : typeRef(a.AttributeClass), target, a.ConstructorArguments.Select(TypedConstantText).ToArray(), new SortedDictionary<string, string>(a.NamedArguments.ToDictionary(p => p.Key, p => TypedConstantText(p.Value)), StringComparer.Ordinal))
    {
        ConstructorId = resolve(a.AttributeConstructor),
        ConstructorValues = a.ConstructorArguments.Select(TypedValue).ToArray(),
        NamedValues = new SortedDictionary<string, ConstantDocument>(a.NamedArguments.ToDictionary(p => p.Key, p => TypedValue(p.Value)), StringComparer.Ordinal)
    };
    public ConstantDocument Value(ITypeSymbol? type, object? value) => new(type?.TypeKind == TypeKind.Enum ? "Enum" : "Primitive", type is null ? null : typeRef(type), Constant(value))
    {
        IsNull = value is null,
        Value = value
    };
    private ConstantDocument TypedValue(TypedConstant constant) => new(constant.Kind.ToString(), constant.Type is null ? null : typeRef(constant.Type), TypedConstantText(constant))
    {
        IsNull = constant.IsNull,
        Value = constant.Kind is TypedConstantKind.Array or TypedConstantKind.Type ? null : constant.Value,
        ReferencedType = constant.Kind == TypedConstantKind.Type && constant.Value is ITypeSymbol referenced ? typeRef(referenced) : null,
        Elements = constant.Kind == TypedConstantKind.Array && !constant.IsNull ? constant.Values.Select(TypedValue).ToArray() : []
    };
    private static string TypedConstantText(TypedConstant constant) => constant.Kind == TypedConstantKind.Array ? constant.IsNull ? "null" : "[" + string.Join(", ", constant.Values.Select(TypedConstantText)) + "]" : constant.Kind == TypedConstantKind.Type ? "typeof(" + constant.Value + ")" : Constant(constant.Value);
    internal static string Constant(object? value) => value switch
    {
        null => "null",
        string s => SymbolDisplay.FormatLiteral(s, true),
        char c => SymbolDisplay.FormatLiteral(c, true),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null"
    };
}
