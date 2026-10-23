using ScriptArt.Compiler;
using ScriptArt.Compiler.Diagnostics;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.Compiler.TypeSystem;
using ScriptArt.GraphModel;

namespace ScriptArt.ProjectSystem;

/// <summary>A graph belonging to a project, with its path relative to the project directory.</summary>
public sealed record ProjectGraph(string RelativePath, GraphDocument Graph);

/// <summary>The outcome of generating C# for a whole project, or of previewing it.</summary>
/// <param name="Changes">The generated files that writing adds, changes, or deletes. Empty when generation failed.</param>
/// <param name="ChangedFiles">Generated files written or removed. Empty when generation failed or was only previewed.</param>
public sealed record ProjectGenerationResult(
    IReadOnlyList<GraphDiagnostic> Diagnostics,
    IReadOnlyList<GeneratedSource> Sources,
    IReadOnlyList<GeneratedFileChange> Changes,
    IReadOnlyList<string> ChangedFiles)
{
    public bool Success => !Diagnostics.Any(diagnostic => diagnostic.IsError);
}

/// <summary>
/// Where a symbol in the project's C# is declared: in a handwritten file, in the code generated from a graph (with the node that
/// produced the declaration, when one did), or in a referenced assembly, which has no source.
/// </summary>
public sealed record ProjectDefinition(string SymbolName, string? FilePath, int Line, int Column, ProjectGraph? Graph, string? NodeId);

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

    // Graph files of removed or renamed graphs, deleted by the next Save once the project file no longer lists them.
    private readonly HashSet<string> obsoleteFiles = new(StringComparer.OrdinalIgnoreCase);

    // Shared with the project's snapshots, so compiling a snapshot reuses the results of unchanged graphs (D-039).
    private readonly GraphCompilationCache compilationCache;

    private VisualProject(string projectFilePath, ProjectDocument document, List<ProjectGraph> graphs, GraphCompilationCache? compilationCache = null, bool isSnapshot = false)
    {
        ProjectFilePath = Path.GetFullPath(projectFilePath);
        Document = document;
        this.graphs = graphs;
        this.compilationCache = compilationCache ?? new GraphCompilationCache();
        IsSnapshot = isSnapshot;
    }

    /// <summary>True for a copy made by <see cref="Snapshot"/>, whose graphs are not saved or changed.</summary>
    public bool IsSnapshot { get; }

    /// <summary>
    /// The source generators the user approved (<see cref="Preferences.ApprovedSourceGenerators"/>). Those of the project's packages
    /// run when its code is verified; others are never loaded (D-050).
    /// </summary>
    public IReadOnlyList<SourceGeneratorAssembly> ApprovedSourceGenerators { get; set; } = [];

    /// <summary>The project's source generators that the user has not approved, so verification does not run them.</summary>
    public IReadOnlyList<string> UnapprovedSourceGenerators() =>
        [.. References.SourceGeneratorPaths.Where(path => !ApprovedSourceGenerators.Any(approved => PreferencesStore.SamePath(approved.Path, path)))];

    // The approved generators of the project's packages, with the properties Godot.NET.Sdk makes visible to them.
    private SourceGeneratorSet? SourceGenerators()
    {
        SourceGeneratorAssembly[] approved = [.. ApprovedSourceGenerators.Where(approved =>
            References.SourceGeneratorPaths.Any(path => PreferencesStore.SamePath(approved.Path, path)))];
        if (approved.Length == 0)
        {
            return null;
        }

        Dictionary<string, string> properties = IsGodotProject
            ? new() { ["GodotProjectDir"] = DirectoryPath + Path.DirectorySeparatorChar, ["GodotSourceGenerators"] = "", ["IsGodotToolsProject"] = "false" }
            : [];
        return new SourceGeneratorSet(approved, properties);
    }

    /// <summary>The cache that makes compiling this project incremental.</summary>
    public GraphCompilationCache CompilationCache => compilationCache;

    /// <summary>
    /// A read-only copy of this project with copied graphs, for compiling, verifying, and generating on a background thread while
    /// the graphs are edited. <paramref name="copy"/> copies one graph; the default is <see cref="GraphDocument.Clone"/>. A copy
    /// must not be changed afterwards. Generating from a snapshot writes generated files as usual; it cannot save graphs.
    /// </summary>
    public VisualProject Snapshot(Func<GraphDocument, GraphDocument>? copy = null)
    {
        copy ??= graph => graph.Clone();
        var document = new ProjectDocument { Name = Document.Name, RootNamespace = Document.RootNamespace, Graphs = [.. Document.Graphs] };
        return new VisualProject(ProjectFilePath, document, [.. graphs.Select(graph => graph with { Graph = copy(graph.Graph) })], compilationCache, isSnapshot: true)
        {
            ApprovedSourceGenerators = ApprovedSourceGenerators,
        };
    }

    private void ThrowIfSnapshot()
    {
        if (IsSnapshot)
        {
            throw new InvalidOperationException("A project snapshot is read-only.");
        }
    }

    public string ProjectFilePath { get; }

    public string DirectoryPath => Path.GetDirectoryName(ProjectFilePath)!;

    public ProjectDocument Document { get; }

    public string Name => Document.Name;

    public string CsprojPath => Path.Combine(DirectoryPath, Document.Name + ".csproj");

    public string GeneratedDirectory => Path.Combine(DirectoryPath, GeneratedFolder);

    public IReadOnlyList<ProjectGraph> Graphs => graphs;

    /// <summary>Every graph document, in project order. Call nodes, type nodes, and type names refer to these.</summary>
    public IReadOnlyList<GraphDocument> GraphDocuments => [.. graphs.Select(graph => graph.Graph)];

    /// <summary>The function graphs, in project order.</summary>
    public IReadOnlyList<GraphDocument> Functions => [.. GraphDocuments.Where(graph => graph.Kind == GraphKind.Function)];

    /// <summary>Compilation settings for every graph in the project.</summary>
    public GraphCompilationOptions CompilationOptions => new(
        Document.RootNamespace,
        GraphDocuments,
        graphs.FirstOrDefault(graph => graph.Graph.Kind == GraphKind.EntryPoint)?.Graph.Name ?? "Program");

    /// <summary>
    /// Adds a function graph named <paramref name="name"/>, containing only its Function Entry node, as
    /// <c>Graphs/{name}.graph.json</c>. It is written by the next <see cref="Save"/>. Refuses a path that is already in the
    /// project or already exists on disk, so an existing file is never overwritten.
    /// </summary>
    public ProjectGraph AddFunction(string name)
    {
        ThrowIfSnapshot();
        GraphDocument function = GraphDocument.CreateEmpty(name, GraphKind.Function);
        function.Nodes.Add(new GraphNode { Id = GraphDocument.NewId(), Type = FunctionNodes.EntryTypeId, X = 0, Y = 0 });
        return AddGraph(function);
    }

    /// <summary>
    /// Adds a method named <paramref name="name"/> to the class or struct graph <paramref name="owner"/>, like <see cref="AddFunction"/>,
    /// as <c>Graphs/{owner}.{name}.graph.json</c>.
    /// </summary>
    public ProjectGraph AddMethod(GraphDocument owner, string name) => AddMember(owner, name, GraphKind.Function);

    /// <summary>
    /// Adds a method to the class graph <paramref name="owner"/> that overrides <paramref name="overridden"/>, with its parameters and
    /// return type (D-047). A parameter named with a C# keyword, such as Godot's <c>@event</c>, is renamed (<c>theEvent</c>), since
    /// graph names are plain identifiers; an override may name its parameters freely.
    /// </summary>
    public ProjectGraph AddOverride(GraphDocument owner, ExternalMethod overridden)
    {
        ArgumentNullException.ThrowIfNull(overridden);
        ProjectGraph method = AddMethod(owner, overridden.Name);
        method.Graph.Parameters.AddRange(overridden.Parameters.Select(parameter => new GraphParameter
        {
            Id = GraphDocument.NewId(),
            Name = GraphCompiler.IsValidIdentifier(parameter.Name) ? parameter.Name : "the" + char.ToUpperInvariant(parameter.Name[0]) + parameter.Name[1..],
            Type = parameter.Type.SourceName,
        }));
        method.Graph.ReturnType = overridden.ReturnType == TypeRef.Void ? null : overridden.ReturnType.SourceName;
        return method;
    }

    /// <summary>
    /// Adds a constructor of the class or struct graph <paramref name="owner"/>, like <see cref="AddMethod"/>. Its
    /// <paramref name="name"/> only names the graph and its files; the generated constructor has the type's name.
    /// </summary>
    public ProjectGraph AddConstructor(GraphDocument owner, string name) => AddMember(owner, name, GraphKind.Constructor);

    private ProjectGraph AddMember(GraphDocument owner, string name, GraphKind kind)
    {
        ThrowIfSnapshot();
        ArgumentNullException.ThrowIfNull(owner);
        if (!graphs.Exists(graph => graph.Graph == owner) || owner.Kind is not (GraphKind.Class or GraphKind.Struct))
        {
            throw new ArgumentException($"'{owner.Name}' is not a class or struct of this project.", nameof(owner));
        }

        GraphDocument member = GraphDocument.CreateEmpty(name, kind);
        member.Owner = owner.Id;
        member.Nodes.Add(new GraphNode { Id = GraphDocument.NewId(), Type = FunctionNodes.EntryTypeId, X = 0, Y = 0 });
        return AddGraph(member, $"{owner.Name}.{name}");
    }

    /// <summary>
    /// Adds a class or struct graph (<paramref name="kind"/>) named <paramref name="name"/>, with no properties, like
    /// <see cref="AddFunction"/>.
    /// </summary>
    public ProjectGraph AddType(string name, GraphKind kind)
    {
        ThrowIfSnapshot();
        if (kind is not (GraphKind.Class or GraphKind.Struct))
        {
            throw new ArgumentException($"{kind} is not a class or struct.", nameof(kind));
        }

        return AddGraph(GraphDocument.CreateEmpty(name, kind));
    }

    /// <summary>
    /// The file <paramref name="graph"/> belongs in: <c>Graphs/{name}.graph.json</c>, or <c>Graphs/{type}.{name}.graph.json</c>
    /// for a method or constructor, so that two types can each have a method with the same name.
    /// </summary>
    public string ExpectedPath(GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        string? owner = graph.Owner is null ? null : graphs.Find(type => type.Graph.Id == graph.Owner)?.Graph.Name;
        return $"{GraphsFolder}/{(owner is null ? "" : owner + ".")}{graph.Name}{GraphSerializer.FileExtension}";
    }

    /// <summary>
    /// Removes <paramref name="graph"/> and, for a class or struct, its methods and constructors. Their files are deleted by the
    /// next <see cref="Save"/>. The entry graph cannot be removed. Returns the removed graphs.
    /// </summary>
    public IReadOnlyList<ProjectGraph> RemoveGraph(GraphDocument graph)
    {
        ThrowIfSnapshot();
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Kind == GraphKind.EntryPoint)
        {
            throw new InvalidOperationException("The program graph cannot be removed; every program starts there.");
        }

        ProjectGraph[] removed = [.. graphs.Where(candidate => candidate.Graph == graph || (candidate.Graph.Owner is not null && candidate.Graph.Owner == graph.Id))];
        foreach (ProjectGraph item in removed)
        {
            graphs.Remove(item);
            Document.Graphs.Remove(item.RelativePath);
            obsoleteFiles.Add(item.RelativePath);
        }

        return removed;
    }

    private ProjectGraph AddGraph(GraphDocument graph, string? fileName = null)
    {
        string kind = graph.Kind.ToString().ToLowerInvariant();
        if (!GraphCompiler.IsValidIdentifier(graph.Name))
        {
            throw new ArgumentException($"'{graph.Name}' is not a valid {kind} name. Use letters, digits, and underscores, starting with a letter.", nameof(graph));
        }

        string relativePath = $"{GraphsFolder}/{fileName ?? graph.Name}{GraphSerializer.FileExtension}";
        if (graphs.Exists(existing => string.Equals(existing.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            || File.Exists(ResolveInside(DirectoryPath, relativePath)))
        {
            throw new IOException($"'{relativePath}' already exists. Choose a different name.");
        }

        var added = new ProjectGraph(relativePath, graph);
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
        (string projectFile, string csproj) = NewProjectFiles(directory, name);
        var document = new ProjectDocument { Name = name, RootNamespace = name };
        string graphPath = $"{GraphsFolder}/Program{GraphSerializer.FileExtension}";
        document.Graphs.Add(graphPath);

        var project = new VisualProject(projectFile, document, [new ProjectGraph(graphPath, CreateStarterGraph())]);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(csproj, CsprojTemplate(document));
        project.Save();
        return project;
    }

    /// <summary>The Godot version new Godot projects target: the oldest supported Godot 4 release (D-047).</summary>
    public const string GodotVersion = "4.4.1";

    /// <summary>
    /// Creates a Godot 4 C# project (D-047): a <c>Godot.NET.Sdk</c> <c>.csproj</c>, a <c>project.godot</c> and a <c>Main.tscn</c>
    /// scene unless the folder already has them, and a <c>Main</c> class deriving from <c>Godot.Node</c> whose <c>_Ready</c> prints a
    /// greeting. The scene runs the script generated from <c>Main</c>.
    /// </summary>
    public static VisualProject CreateGodot(string directory, string name)
    {
        (string projectFile, string csproj) = NewProjectFiles(directory, name);
        GraphDocument main = GraphDocument.CreateEmpty("Main", GraphKind.Class);
        main.BaseType = "Godot.Node";
        GraphDocument ready = GraphDocument.CreateEmpty("_Ready", GraphKind.Function);
        ready.Owner = main.Id;
        var entry = new GraphNode { Id = GraphDocument.NewId(), Type = FunctionNodes.EntryTypeId, X = 0, Y = 0 };
        var print = new GraphNode { Id = GraphDocument.NewId(), Type = "dotnet:M:Godot.GD.Print(System.String)", X = 260, Y = 0 };
        var text = new GraphNode { Id = GraphDocument.NewId(), Type = "literal.string", X = 0, Y = 120 };
        text.Properties["value"] = "Hello from ScriptArt Code!";
        ready.Nodes.AddRange([entry, print, text]);
        ready.Connections.Add(new GraphConnection(new PinRef(entry.Id, "then"), new PinRef(print.Id, "exec")));
        ready.Connections.Add(new GraphConnection(new PinRef(text.Id, "value"), new PinRef(print.Id, "what")));

        var document = new ProjectDocument { Name = name, RootNamespace = name };
        List<ProjectGraph> graphs = [
            new($"{GraphsFolder}/Main{GraphSerializer.FileExtension}", main),
            new($"{GraphsFolder}/Main._Ready{GraphSerializer.FileExtension}", ready)];
        document.Graphs.AddRange(graphs.Select(graph => graph.RelativePath));

        var project = new VisualProject(projectFile, document, graphs);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(csproj, GodotCsprojTemplate(document));
        WriteIfMissing(Path.Combine(directory, "project.godot"), $"""
            ; Godot project settings, created by ScriptArt Code. Godot keeps this file up to date.

            config_version=5

            [application]

            config/name="{name}"
            run/main_scene="res://Main.tscn"
            config/features=PackedStringArray("{GodotVersion[..3]}", "C#")

            [dotnet]

            project/assembly_name="{name}"

            """);
        WriteIfMissing(Path.Combine(directory, "Main.tscn"), $"""
            [gd_scene load_steps=2 format=3]

            [ext_resource type="Script" path="res://{GeneratedFolder}/Main.cs" id="1"]

            [node name="Main" type="Node"]
            script = ExtResource("1")

            """);
        project.Save();
        return project;

        static void WriteIfMissing(string path, string text)
        {
            if (!File.Exists(path))
            {
                AtomicFile.WriteAllText(path, text.ReplaceLineEndings("\n"));
            }
        }
    }

    // The project and .csproj files of a new project, after checking the name and that neither exists.
    private static (string ProjectFile, string Csproj) NewProjectFiles(string directory, string name)
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

        return (projectFile, csproj);
    }

    /// <summary>
    /// True when the <c>.csproj</c> uses <c>Godot.NET.Sdk</c>: a Godot 4 C# project, built as a library that the Godot editor loads,
    /// with no <c>Main</c> method.
    /// </summary>
    public bool IsGodotProject
    {
        get
        {
            try
            {
                return System.Xml.Linq.XDocument.Load(CsprojPath).Root?.Attribute("Sdk")?.Value.StartsWith("Godot.NET.Sdk", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return false;
            }
        }
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

    /// <summary>
    /// The text <see cref="Save"/> would write for the project file and each graph, by full path, without writing anything. Autosave
    /// keeps these as recovery copies.
    /// </summary>
    public IReadOnlyDictionary<string, string> SerializedFiles()
    {
        Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            [ProjectFilePath] = ProjectSerializer.Serialize(Document),
        };
        foreach (ProjectGraph graph in graphs)
        {
            files[ResolveInside(DirectoryPath, graph.RelativePath)] = GraphSerializer.Serialize(graph.Graph);
        }

        return files;
    }

    /// <summary>
    /// Writes the project file and every graph. A renamed graph moves to the file its name now calls for (see
    /// <see cref="ExpectedPath"/>) unless that name is not a valid identifier or the file is taken; the old file, and the files of
    /// removed graphs, are deleted only after the project file stops listing them.
    /// </summary>
    public void Save()
    {
        ThrowIfSnapshot();
        for (int index = 0; index < graphs.Count; index++)
        {
            ProjectGraph graph = graphs[index];
            string expected = ExpectedPath(graph.Graph);
            if (expected != graph.RelativePath && GraphCompiler.IsValidIdentifier(graph.Graph.Name) && IsFree(expected, graph))
            {
                obsoleteFiles.Add(graph.RelativePath);
                obsoleteFiles.Remove(expected);
                Document.Graphs[Document.Graphs.IndexOf(graph.RelativePath)] = expected;
                graphs[index] = graph = graph with { RelativePath = expected };
            }

            GraphSerializer.Save(ResolveInside(DirectoryPath, graph.RelativePath), graph.Graph);
        }

        AtomicFile.WriteAllText(ProjectFilePath, ProjectSerializer.Serialize(Document));
        foreach (string obsolete in obsoleteFiles.Where(path => !graphs.Exists(graph => string.Equals(graph.RelativePath, path, StringComparison.OrdinalIgnoreCase))))
        {
            File.Delete(ResolveInside(DirectoryPath, obsolete));
        }

        obsoleteFiles.Clear();
    }

    // A path another graph does not use, and that is not on disk unless only the case differs or it is a file being deleted.
    private bool IsFree(string path, ProjectGraph moving) =>
        !graphs.Exists(other => other != moving && string.Equals(other.RelativePath, path, StringComparison.OrdinalIgnoreCase))
        && (!File.Exists(ResolveInside(DirectoryPath, path)) || obsoleteFiles.Contains(path) || string.Equals(path, moving.RelativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The assembly <c>dotnet build -c <paramref name="configuration"/></c> produces: <c>bin/{configuration}/{TargetFramework}/{AssemblyName}.dll</c>,
    /// with the <c>.csproj</c>'s values, or <c>net10.0</c> and the project name where it sets none.
    /// </summary>
    public string OutputAssemblyPath(string configuration)
    {
        string framework = "net10.0";
        string assembly = Name;
        try
        {
            System.Xml.Linq.XDocument csproj = System.Xml.Linq.XDocument.Load(CsprojPath);
            string? Value(string name) => csproj.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
            framework = Value("TargetFramework") ?? framework;
            assembly = Value("AssemblyName") ?? assembly;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Without a readable project file the SDK defaults apply.
        }

        return Path.Combine(DirectoryPath, "bin", configuration, framework, assembly + ".dll");
    }

    /// <summary>The assemblies the <c>.csproj</c> references, read from disk on each call because restore and the user can change them.</summary>
    public ProjectReferences References => ProjectReferences.Resolve(CsprojPath);

    /// <summary>Compiles every graph in memory without writing anything.</summary>
    public (List<GraphDiagnostic> Diagnostics, List<GeneratedSource> Sources) CompileGraphs(GraphCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        List<GraphDiagnostic> diagnostics = [];
        List<GeneratedSource> sources = [];
        foreach (GraphCompilationResult result in compilationCache.CompileAll(compiler, [.. graphs.Select(graph => (graph.Graph, graph.RelativePath))], CompilationOptions, immutableGraphs: IsSnapshot))
        {
            diagnostics.AddRange(result.Diagnostics);
            if (result.Source is not null)
            {
                sources.Add(result.Source);
            }
        }

        return (diagnostics, sources);
    }

    /// <summary>
    /// Compiles every graph and verifies the generated code together with the project's handwritten C# using Roslyn, like
    /// <see cref="Generate"/>, and lists the generated files writing would change, without writing anything.
    /// </summary>
    /// <param name="unsavedFiles">Text of handwritten files that are edited but not saved, by full path; it replaces their text on disk.</param>
    public ProjectGenerationResult PreviewGeneration(GraphCompiler compiler, IReadOnlyDictionary<string, string>? unsavedFiles = null)
    {
        (List<GraphDiagnostic> diagnostics, List<GeneratedSource> sources) = CompileGraphs(compiler);
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            return new ProjectGenerationResult(diagnostics, sources, [], []);
        }

        diagnostics.AddRange(RoslynVerifier.Verify(Document.Name, VerificationSources(sources, unsavedFiles), isExecutable: !IsGodotProject, References.AssemblyPaths, SourceGenerators()));
        if (diagnostics.Any(diagnostic => diagnostic.IsError))
        {
            return new ProjectGenerationResult(diagnostics, sources, [], []);
        }

        diagnostics.AddRange(GeneratedFileWriter.Plan(GeneratedDirectory, sources, out IReadOnlyList<GeneratedFileChange> changes));
        return new ProjectGenerationResult(diagnostics, sources, changes, []);
    }

    /// <summary>
    /// Compiles every graph, verifies the generated code together with the project's handwritten C# using Roslyn,
    /// and only then writes the generated files. Nothing is written when any step reports an error.
    /// </summary>
    /// <param name="unsavedFiles">As for <see cref="PreviewGeneration"/>. Only verification sees them; they are not saved.</param>
    public ProjectGenerationResult Generate(GraphCompiler compiler, IReadOnlyDictionary<string, string>? unsavedFiles = null)
    {
        ProjectGenerationResult preview = PreviewGeneration(compiler, unsavedFiles);
        if (!preview.Success)
        {
            return preview;
        }

        if (!File.Exists(CsprojPath))
        {
            // The .csproj is user-owned and only created when missing.
            AtomicFile.WriteAllText(CsprojPath, CsprojTemplate(Document));
        }

        IReadOnlyList<GraphDiagnostic> conflicts = GeneratedFileWriter.Write(GeneratedDirectory, preview.Sources, out IReadOnlyList<string> changed);
        return preview with { Diagnostics = [.. preview.Diagnostics, .. conflicts], ChangedFiles = changed };
    }

    /// <summary>
    /// The declaration of the symbol at <paramref name="position"/> (a zero-based character offset) in <paramref name="path"/>,
    /// a handwritten file or the generated file of a graph (<see cref="GeneratedPath"/>); null when there is no symbol there.
    /// Graphs with errors generate no code, so their declarations are not found.
    /// </summary>
    public ProjectDefinition? FindDefinition(GraphCompiler compiler, string path, int position, IReadOnlyDictionary<string, string>? unsavedFiles = null)
    {
        (_, List<GeneratedSource> sources) = CompileGraphs(compiler);
        List<VerificationSource> verification = VerificationSources(sources, unsavedFiles);
        if (CodeNavigator.FindDefinition(verification, Path.GetFullPath(path), position, References.AssemblyPaths) is not { } definition)
        {
            return null;
        }

        if (verification.FirstOrDefault(source => source.Path == definition.Path && source.Generated is not null) is { Generated: { } generated } file)
        {
            return new ProjectDefinition(definition.SymbolName, definition.Path, definition.Line, definition.Column,
                graphs.First(graph => graph.Graph.Id == file.GraphId), generated.FindLine(definition.Line)?.NodeId);
        }

        return new ProjectDefinition(definition.SymbolName, definition.Path, definition.Line, definition.Column, null, null);
    }

    /// <summary>The full path <paramref name="graph"/>'s code is generated to.</summary>
    public string GeneratedPath(GraphDocument graph) => Path.Combine(GeneratedDirectory, GraphCompiler.GeneratedFileName(graph, GraphDocuments));

    /// <summary>The graph whose code is generated to the full path <paramref name="path"/>, if any.</summary>
    public ProjectGraph? GraphForGeneratedPath(string path) =>
        graphs.FirstOrDefault(graph => string.Equals(GeneratedPath(graph.Graph), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    // Generated sources with their graphs and source maps, and the handwritten files, unsaved text first.
    private List<VerificationSource> VerificationSources(IReadOnlyList<GeneratedSource> sources, IReadOnlyDictionary<string, string>? unsavedFiles)
    {
        List<VerificationSource> verification = [.. sources.Select(source => new VerificationSource(
            Path.Combine(GeneratedDirectory, source.RelativePath),
            source.Text,
            source,
            graphs.First(graph => GraphCompiler.GeneratedFileName(graph.Graph, GraphDocuments) == source.RelativePath).Graph.Id))];
        verification.AddRange(HandwrittenSources().Select(path =>
            new VerificationSource(path, unsavedFiles is not null && unsavedFiles.TryGetValue(path, out string? text) ? text : File.ReadAllText(path))));
        return verification;
    }

    /// <summary>
    /// Creates a handwritten C# file <c>{className}.cs</c> in the project directory, holding an empty static class in the root
    /// namespace, and returns its full path. Refuses a name that is not a C# identifier or a file that already exists.
    /// </summary>
    public string AddSourceFile(string className)
    {
        if (!GraphCompiler.IsValidIdentifier(className))
        {
            throw new ArgumentException($"'{className}' is not a valid class name. Use letters, digits, and underscores, starting with a letter.", nameof(className));
        }

        string path = Path.Combine(DirectoryPath, className + ".cs");
        if (File.Exists(path))
        {
            throw new IOException($"'{className}.cs' already exists. Choose a different name.");
        }

        AtomicFile.WriteAllText(path, CodeNavigator.NewClassFile(Document.RootNamespace, className));
        return path;
    }

    /// <summary>
    /// Saves a handwritten C# file of the project. Refuses paths outside the project, generated files (their graph owns them),
    /// and text that starts with the generator's header, which would hand the file to the generator to overwrite or delete.
    /// </summary>
    public void SaveSourceFile(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{path}' is not a C# file of project {Name}.", nameof(path));
        }

        if (File.Exists(fullPath) && GeneratedSource.IsGeneratedText(File.ReadAllText(fullPath)))
        {
            throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' is generated from a graph. Edit the graph instead.");
        }

        if (GeneratedSource.IsGeneratedText(text))
        {
            throw new InvalidOperationException($"'{Path.GetFileName(fullPath)}' starts with the generator's header, so generating code could overwrite or delete it. Remove the header to save it.");
        }

        AtomicFile.WriteAllText(fullPath, text);
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

    /// <summary>The <c>.csproj</c> of a new Godot project: <c>Godot.NET.Sdk</c> at <see cref="GodotVersion"/>, which brings GodotSharp.</summary>
    public static string GodotCsprojTemplate(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return $"""
            <Project Sdk="Godot.NET.Sdk/{GodotVersion}">

              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <RootNamespace>{document.RootNamespace}</RootNamespace>
                <Nullable>enable</Nullable>
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
    internal static string ResolveInside(string directory, string relativePath)
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
