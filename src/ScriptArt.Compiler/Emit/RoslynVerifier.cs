using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ScriptArt.Compiler.Diagnostics;
using GraphSeverity = ScriptArt.Compiler.Diagnostics.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace ScriptArt.Compiler.Emit;

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
/// This is a fast pre-check, not a replacement for <c>dotnet build</c>: package assemblies are passed in by the caller once restored,
/// and project references are not resolved.
/// </remarks>
public static class RoslynVerifier
{
    private static readonly Lazy<MetadataReference[]> RuntimeReferenceList = new(LoadRuntimeReferences);

    // Reading an assembly's metadata is the costly part of setting up a compilation; a file is read again only when it changes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Written, long Length), MetadataReference> FileReferences = new();

    /// <summary>The framework assemblies of the running runtime.</summary>
    internal static IReadOnlyList<MetadataReference> RuntimeReferences => RuntimeReferenceList.Value;

    /// <param name="referencePaths">Assemblies the project references besides the framework, such as NuGet package assemblies.</param>
    /// <param name="generators">
    /// Source generators the user approved, run before checking so that code using what they generate (Godot's <c>SignalName</c>)
    /// compiles as in a build. Their own errors are reported too. A generator that cannot be run is reported as a warning.
    /// </param>
    public static IReadOnlyList<GraphDiagnostic> Verify(string assemblyName, IReadOnlyList<VerificationSource> sources, bool isExecutable,
        IReadOnlyList<string>? referencePaths = null, SourceGeneratorSet? generators = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        CSharpCompilation compilation = CreateCompilation(assemblyName, sources, isExecutable, referencePaths);

        List<GraphDiagnostic> results = [];
        IEnumerable<Diagnostic> generatorDiagnostics = [];
        if (generators is { Assemblies.Count: > 0 })
        {
            List<ISourceGenerator> loaded = [];
            foreach (SourceGeneratorAssembly assembly in generators.Assemblies)
            {
                if (SourceGeneratorLoader.TryLoad(assembly, out var found, out string? problem))
                {
                    loaded.AddRange(found);
                }
                else
                {
                    results.Add(new GraphDiagnostic(DiagnosticIds.SourceGeneratorNotRun, GraphSeverity.Warning, problem, null, null));
                }
            }

            GeneratorDriver driver = CSharpGeneratorDriver.Create(loaded, parseOptions: compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions,
                optionsProvider: new BuildPropertyOptionsProvider(generators.BuildProperties));
            driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation generated, out ImmutableArray<Diagnostic> produced);
            compilation = (CSharpCompilation)generated;
            generatorDiagnostics = produced;
        }

        foreach (Diagnostic diagnostic in generatorDiagnostics.Concat(compilation.GetDiagnostics()))
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

    /// <summary>An in-memory compilation of <paramref name="sources"/>, as verification and code navigation see the project.</summary>
    internal static CSharpCompilation CreateCompilation(string assemblyName, IReadOnlyList<VerificationSource> sources, bool isExecutable,
        IReadOnlyList<string>? referencePaths)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        return CSharpCompilation.Create(
            assemblyName,
            [.. sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, parseOptions, source.Path))],
            [.. RuntimeReferences, .. (referencePaths ?? []).Select(FileReference)],
            new CSharpCompilationOptions(
                isExecutable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>The metadata of the assembly at <paramref name="path"/>, read once per version of the file.</summary>
    internal static MetadataReference FileReference(string path)
    {
        var file = new FileInfo(path);
        (string Path, DateTime Written, long Length) key = (file.FullName, file.LastWriteTimeUtc, file.Exists ? file.Length : -1);
        if (FileReferences.TryGetValue(key, out MetadataReference? cached))
        {
            return cached;
        }

        // A rebuilt project reference replaces its assembly; earlier versions of the file are never used again.
        foreach (var stale in FileReferences.Keys.Where(other => other.Path == key.Path))
        {
            FileReferences.TryRemove(stale, out _);
        }

        return FileReferences.GetOrAdd(key, _ => MetadataReference.CreateFromFile(key.Path));
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
