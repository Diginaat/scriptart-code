using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using GraphSeverity = VisualStudioLikeIDE.Compiler.Diagnostics.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace VisualStudioLikeIDE.Compiler.Emit;

/// <summary>A C# file that takes part in verification.</summary>
/// <param name="Path">The file's path, reported in diagnostics.</param>
/// <param name="Generated">The generated source and its source map when the file was generated from a graph; null for handwritten files.</param>
/// <param name="GraphId">The graph that produced the file, for generated files.</param>
public sealed record VerificationSource(string Path, string Text, GeneratedSource? Generated = null, string? GraphId = null);

/// <summary>
/// Compiles C# sources in memory with Roslyn to catch semantic errors before anything is written or built.
/// Diagnostics in generated files are mapped back to the graph node that produced the code.
/// </summary>
/// <remarks>
/// References come from the running .NET runtime, which matches the generated projects' target framework (net10.0).
/// This is a fast pre-check, not a replacement for <c>dotnet build</c>: package and project references are not resolved here.
/// </remarks>
public static class RoslynVerifier
{
    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(LoadRuntimeReferences);

    public static IReadOnlyList<GraphDiagnostic> Verify(string assemblyName, IReadOnlyList<VerificationSource> sources, bool isExecutable)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        SyntaxTree[] trees = [.. sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, parseOptions, source.Path))];

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            RuntimeReferences.Value,
            new CSharpCompilationOptions(
                isExecutable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        List<GraphDiagnostic> results = [];
        foreach (Diagnostic diagnostic in compilation.GetDiagnostics())
        {
            if (diagnostic.Severity is not (RoslynSeverity.Error or RoslynSeverity.Warning) || diagnostic.IsSuppressed)
            {
                continue;
            }

            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            VerificationSource? source = sources.FirstOrDefault(candidate => candidate.Path == span.Path);
            string? nodeId = source?.Generated?.FindLine(line)?.NodeId;

            results.Add(new GraphDiagnostic(
                diagnostic.Id,
                diagnostic.Severity == RoslynSeverity.Error ? GraphSeverity.Error : GraphSeverity.Warning,
                diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                source?.GraphId,
                nodeId,
                FilePath: span.Path,
                Line: line));
        }

        return results;
    }

    private static MetadataReference[] LoadRuntimeReferences()
    {
        string paths = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        return [.. paths
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "mscorlib.dll" or "netstandard.dll"))
            .Order(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path))];
    }
}
