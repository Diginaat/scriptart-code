using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ScriptArt.Compiler.Emit;

/// <summary>Where a symbol is declared.</summary>
/// <param name="SymbolName">The symbol as C# shows it, such as <c>Program.Greet(string)</c>.</param>
/// <param name="Path">The source file that declares it; null for a symbol of a referenced assembly, which has no source.</param>
/// <param name="Line">One-based line of the declaration in <paramref name="Path"/>.</param>
/// <param name="Column">One-based column of the declared name.</param>
public sealed record SymbolDefinition(string SymbolName, string? Path, int Line, int Column);

/// <summary>Code navigation over a project's generated and handwritten C#, with Roslyn's semantic model.</summary>
public static class CodeNavigator
{
    /// <summary>
    /// The declaration of the symbol at <paramref name="position"/> (a zero-based character offset) in the file
    /// <paramref name="path"/>, which must be one of <paramref name="sources"/>; null when there is no symbol there.
    /// </summary>
    public static SymbolDefinition? FindDefinition(IReadOnlyList<VerificationSource> sources, string path, int position,
        IReadOnlyList<string>? referencePaths = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        CSharpCompilation compilation = RoslynVerifier.CreateCompilation("Navigation", sources, isExecutable: true, referencePaths);
        if (compilation.SyntaxTrees.FirstOrDefault(tree => tree.FilePath == path) is not { } tree)
        {
            return null;
        }

        SyntaxNode root = tree.GetRoot();
        SemanticModel model = compilation.GetSemanticModel(tree);

        // A caret right after a name, as after typing it or a double-click, still names it.
        foreach (int candidate in new[] { position, position - 1 })
        {
            if (candidate < 0 || candidate >= root.FullSpan.End)
            {
                continue;
            }

            SyntaxToken token = root.FindToken(candidate);
            bool isName = token.IsKind(SyntaxKind.IdentifierToken) || SyntaxFacts.IsPredefinedType(token.Kind()) || token.IsKind(SyntaxKind.ThisKeyword);
            if (!isName || token.Parent is not { } node)
            {
                continue;
            }

            if (SymbolAt(model, node) is { } symbol)
            {
                return Describe(symbol);
            }
        }

        return null;
    }

    private static ISymbol? SymbolAt(SemanticModel model, SyntaxNode node)
    {
        if (model.GetDeclaredSymbol(node) is { } declared)
        {
            return declared;
        }

        // The name in `new Point()` binds to the constructor through the whole creation expression.
        SyntaxNode target = node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node ? creation : node;
        SymbolInfo info = model.GetSymbolInfo(target);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private static SymbolDefinition Describe(ISymbol symbol)
    {
        symbol = symbol is IMethodSymbol { ReducedFrom: { } extension } ? extension : symbol.OriginalDefinition;

        // A parameterless constructor nobody wrote has no source of its own; its type does.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true } constructor)
        {
            symbol = constructor.ContainingType;
        }

        string name = symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        if (symbol.Locations.FirstOrDefault(location => location.IsInSource) is not { } location)
        {
            return new SymbolDefinition(name, null, 0, 0);
        }

        FileLinePositionSpan span = location.GetLineSpan();
        return new SymbolDefinition(name, span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    /// <summary>
    /// The text of a new handwritten C# file: an empty <c>internal static class</c> named <paramref name="className"/> in
    /// <paramref name="rootNamespace"/>, laid out like generated code.
    /// </summary>
    public static string NewClassFile(string rootNamespace, string className)
    {
        if (!GraphCompiler.IsValidNamespace(rootNamespace) || !GraphCompiler.IsValidIdentifier(className))
        {
            throw new ArgumentException($"'{rootNamespace}.{className}' is not a valid C# class name.", nameof(className));
        }

        CompilationUnitSyntax unit = CompilationUnit()
            .AddMembers(FileScopedNamespaceDeclaration(ParseName(rootNamespace)).AddMembers(
                ClassDeclaration(className).AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword))))
            .NormalizeWhitespace("    ", "\n");
        ClassDeclarationSyntax declaration = unit.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        unit = unit.ReplaceNode(declaration, declaration.WithLeadingTrivia(declaration.GetLeadingTrivia().Insert(0, EndOfLine("\n"))));
        return unit.ToFullString() + "\n";
    }
}
