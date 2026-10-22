using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Diagnostics;
using VisualStudioLikeIDE.Compiler.Emit;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.ProjectSystem;

/// <summary>A graph belonging to a project, with its path relative to the project directory.</summary>
public sealed record ProjectGraph(string RelativePath, GraphDocument Graph);

/// <summary>The outcome of generating C# for a whole project.</summary>
/// <param name="ChangedFiles">Generated files written or removed. Empty when generation failed.</param>
public sealed record ProjectGenerationResult(
    IReadOnlyList<GraphDiagnostic> Diagnostics,
    IReadOnlyList<GeneratedSource> Sources,
    IReadOnlyList<string> ChangedFiles)
{
    public bool Success => !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// A visual project on disk. Layout:
/// <code>
/// MyApp/
///   MyApp.vside          project file (this IDE)
///   MyApp.csproj          SDK-style project, created once, then owned by the user
///   Graphs/*.graph.json   graph documents
///   Generated/*.g.cs      generated code, owned by the generator
///   (any other *.cs)      handwritten code, never modified by the IDE
/// </code>
/// </summary>
public sealed class VisualProject
{
    public const string FileExtension = ".vside";
    public const string GraphsFolder = "Graphs";
    public const string GeneratedFolder = "Generated";

    private readonly List<ProjectGraph> graphs;

    private VisualProject(string projectFilePath, ProjectDocument document, List<ProjectGraph> graphs)
    {
        ProjectFilePath = Path.GetFullPath(projectFilePath);
        Document = document;
        this.graphs = graphs;
    }

    public string ProjectFilePath { get; }

    public string DirectoryPath => Path.GetDirectoryName(ProjectFilePath)!;

    public ProjectDocument Document { get; }

    public string Name => Document.Name;

    public string CsprojPath => Path.Combine(DirectoryPath, Document.Name + ".csproj");

    public string GeneratedDirectory => Path.Combine(DirectoryPath, GeneratedFolder);

    public IReadOnlyList<ProjectGraph> Graphs => graphs;

    /// <summary>The function graphs, in project order. Call nodes in every graph refer to these.</summary>
    public IReadOnlyList<GraphDocument> Functions => [.. graphs.Select(graph => graph.Graph).Where(graph => graph.Kind == GraphKind.Function)];

    /// <summary>Compilation settings for every graph in the project.</summary>
    public GraphCompilationOptions CompilationOptions => new(
        Document.RootNamespace,
        Functions,
        graphs.FirstOrDefault(graph => graph.Graph.Kind == GraphKind.EntryPoint)?.Graph.Name ?? "Program");

    /// <summary>
    /// Adds a function graph named <paramref name="name"/>, containing only its Function Entry node, as
    /// <c>Graphs/{name}.graph.json</c>. It is written by the next <see cref="Save"/>. Refuses a path that is already in the
    /// project or already exists on disk, so an existing file is never overwritten.
    /// </summary>
    public ProjectGraph AddFunction(string name)
    {
        if (!GraphCompiler.IsValidIdentifier(name))
        {
            throw new ArgumentException($"'{name}' is not a valid function name. Use letters, digits, and underscores, starting with a letter.", nameof(name));
        }

        string relativePath = $"{GraphsFolder}/{name}{GraphSerializer.FileExtension}";
        if (graphs.Exists(graph => string.Equals(graph.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            || File.Exists(ResolveInside(DirectoryPath, relativePath)))
        {
            throw new IOException($"'{relativePath}' already exists. Choose a different name.");
        }

        GraphDocument function = GraphDocument.CreateEmpty(name, GraphKind.Function);
        function.Nodes.Add(new GraphNode { Id = GraphDocument.NewId(), Type = FunctionNodes.EntryTypeId, X = 0, Y = 0 });
        var added = new ProjectGraph(relativePath, function);
        graphs.Add(added);
        Document.Graphs.Add(relativePath);
        return added;
    }

    /// <summary>
    /// Creates a new console project in <paramref name="directory"/> with an entry graph that prints a greeting.
    /// Refuses to touch a directory that already contains a project with that name.
    /// </summary>
    public static VisualProject Create(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!GraphCompiler.IsValidNamespace(name))
        {
            throw new ArgumentException($"'{name}' is not a valid project name. Use letters, digits, and underscores, starting with a letter.", nameof(name));
        }

        string projectFile = Path.Combine(directory, name + FileExtension);
        string csproj = Path.Combine(directory, name + ".csproj");
        foreach (string existing in new[] { projectFile, csproj })
        {
            if (File.Exists(existing))
            {
                throw new IOException($"'{existing}' already exists. Choose an empty folder or a different name.");
            }
        }

        var document = new ProjectDocument { Name = name, RootNamespace = name };
        string graphPath = $"{GraphsFolder}/Program{GraphSerializer.FileExtension}";
        document.Graphs.Add(graphPath);

        var project = new VisualProject(projectFile, document, [new ProjectGraph(graphPath, CreateStarterGraph())]);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(csproj, CsprojTemplate(document));
        project.Save();
        return project;
    }

    public static VisualProject Load(string projectFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ProjectDocument document;
        try
        {
            document = ProjectSerializer.Deserialize(File.ReadAllText(projectFilePath));
        }
        catch (GraphFormatException exception)
        {
            throw new GraphFormatException($"{projectFilePath}: {exception.Message}", exception);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!;
        List<ProjectGraph> graphs = [.. document.Graphs.Select(relative =>
            new ProjectGraph(relative, GraphSerializer.Load(ResolveInside(directory, relative))))];
        return new VisualProject(projectFilePath, document, graphs);
    }

    /// <summary>Writes the project file and every graph.</summary>
    public void Save()
    {
        foreach (ProjectGraph graph in graphs)
        {
            GraphSerializer.Save(ResolveInside(DirectoryPath, graph.RelativePath), graph.Graph);
        }

        AtomicFile.WriteAllText(ProjectFilePath, ProjectSerializer.Serialize(Document));
    }

    /// <summary>The assemblies the <c>.csproj</c> references, read from disk on each call because restore and the user can change them.</summary>
    public ProjectReferences References => ProjectReferences.Resolve(CsprojPath);

    /// <summary>Compiles every graph in memory without writing anything.</summary>
    public (List<GraphDiagnostic> Diagnostics, List<GeneratedSource> Sources) CompileGraphs(GraphCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        GraphCompilationOptions options = CompilationOptions;
        List<GraphDiagnostic> diagnostics = [];
        List<GeneratedSource> sources = [];
        foreach (ProjectGraph graph in graphs)
        {
            GraphCompilationResult result = compiler.Compile(graph.Graph, options, graph.RelativePath);
            diagnostics.AddRange(result.Diagnostics);
            if (result.Source is not null)
            {
                sources.Add(result.Source);
            }
        }

        return (diagnostics, sources);
    }

    /// <summary>
    /// Compiles every graph, verifies the generated code together with the project's handwritten C# using Roslyn,
    /// and only then writes the generated files. Nothing is written when any step reports an error.
    /// </summary>
    public ProjectGenerationResult Generate(GraphCompiler compiler)
    {
        (List<GraphDiagnostic> diagnostics, List<GeneratedSource> sources) = CompileGraphs(compiler);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            return new ProjectGenerationResult(diagnostics, sources, []);
        }

        List<VerificationSource> verification = [.. sources.Select(source => new VerificationSource(
            Path.Combine(GeneratedDirectory, source.RelativePath),
            source.Text,
            source,
            graphs.First(graph => GraphCompiler.GeneratedFileName(graph.Graph) == source.RelativePath).Graph.Id))];
        verification.AddRange(HandwrittenSources().Select(path => new VerificationSource(path, File.ReadAllText(path))));
        diagnostics.AddRange(RoslynVerifier.Verify(Document.Name, verification, isExecutable: true, References.AssemblyPaths));
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            return new ProjectGenerationResult(diagnostics, sources, []);
        }

        if (!File.Exists(CsprojPath))
        {
            // The .csproj is user-owned and only created when missing.
            AtomicFile.WriteAllText(CsprojPath, CsprojTemplate(Document));
        }

        diagnostics.AddRange(GeneratedFileWriter.Write(GeneratedDirectory, sources, out IReadOnlyList<string> changed));
        return new ProjectGenerationResult(diagnostics, sources, changed);
    }

    /// <summary>C# files in the project that the generator does not own.</summary>
    public IEnumerable<string> HandwrittenSources()
    {
        string bin = Path.Combine(DirectoryPath, "bin") + Path.DirectorySeparatorChar;
        string obj = Path.Combine(DirectoryPath, "obj") + Path.DirectorySeparatorChar;
        string generated = GeneratedDirectory + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(DirectoryPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(bin, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(obj, StringComparison.OrdinalIgnoreCase)
                && !(path.StartsWith(generated, StringComparison.OrdinalIgnoreCase) && GeneratedSource.IsGeneratedText(File.ReadAllText(path))))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>The SDK-style project written for new projects. Generated and handwritten files are picked up by the default globs.</summary>
    public static string CsprojTemplate(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>{document.RootNamespace}</RootNamespace>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
              </PropertyGroup>

            </Project>

            """.ReplaceLineEndings("\n");
    }

    private static GraphDocument CreateStarterGraph()
    {
        GraphDocument graph = GraphDocument.CreateEmpty("Program", GraphKind.EntryPoint);
        var entry = new GraphNode { Id = GraphDocument.NewId(), Type = BuiltInNodes.EntryTypeId, X = 0, Y = 0 };
        var print = new GraphNode { Id = GraphDocument.NewId(), Type = "console.write-line", X = 260, Y = 0 };
        var text = new GraphNode { Id = GraphDocument.NewId(), Type = "literal.string", X = 0, Y = 120 };
        text.Properties["value"] = "Hello, World!";
        graph.Nodes.AddRange([entry, print, text]);
        graph.Connections.Add(new GraphConnection(new PinRef(entry.Id, "then"), new PinRef(print.Id, "exec")));
        graph.Connections.Add(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(print.Id, "text")));
        return graph;
    }

    /// <summary>Resolves a project-relative path and rejects paths that escape the project directory.</summary>
    private static string ResolveInside(string directory, string relativePath)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphFormatException($"Graph path '{relativePath}' points outside the project directory.");
        }

        return full;
    }
}
