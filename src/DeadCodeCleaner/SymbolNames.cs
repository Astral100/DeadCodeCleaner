using Microsoft.CodeAnalysis;

namespace DeadCodeCleaner;

public static class SymbolNames
{
    /// <summary>Fully qualified, parameter-free name: Namespace.Type.Nested.Member (constructors as ".ctor").</summary>
    public static string Fqn(ISymbol symbol)
    {
        var parts = new Stack<string>();
        var cur = symbol;
        while (cur != null && cur.Kind != SymbolKind.Namespace && cur.Kind != SymbolKind.NetModule
               && cur.Kind != SymbolKind.Assembly)
        {
            var name = cur is IMethodSymbol m && m.MethodKind == MethodKind.Constructor ? ".ctor"
                     : cur is IMethodSymbol sm && sm.MethodKind == MethodKind.StaticConstructor ? ".cctor"
                     : cur.Name;
            parts.Push(name);
            cur = cur.ContainingSymbol;
        }
        var ns = (cur as INamespaceSymbol)?.IsGlobalNamespace == false
            ? ((INamespaceSymbol)cur!).ToDisplayString() + "."
            : "";
        return ns + string.Join(".", parts);
    }

    /// <summary>Display name including method parameters, for disambiguation output.</summary>
    public static string Detailed(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    public static string Kind(ISymbol s) => s switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => s.Kind.ToString().ToLowerInvariant(),
    };
}
