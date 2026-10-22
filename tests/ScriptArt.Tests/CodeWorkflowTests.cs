using ScriptArt.Compiler;
using ScriptArt.Compiler.Emit;
using ScriptArt.Compiler.Nodes;
using ScriptArt.GraphModel;
using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary>Generation previews, handwritten files, and Go to Definition across graphs and handwritten code.</summary>
public sealed class CodeWorkflowTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    [Fact]
    public void PreviewListsChangesWithoutWritingAnything()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");

        ProjectGenerationResult first = project.PreviewGeneration(Compiler);

        Assert.True(first.Success);
        GeneratedFileChange added = Assert.Single(first.Changes);
        Assert.Equal(GeneratedFileChangeKind.Added, added.Kind);
        Assert.Equal(Path.Combine(project.GeneratedDirectory, "Program.g.cs"), added.Path);
        Assert.Empty(first.ChangedFiles);
        Assert.False(Directory.Exists(project.GeneratedDirectory));

        Assert.Equal([added.Path], project.Generate(Compiler).ChangedFiles);
        Assert.Empty(project.PreviewGeneration(Compiler).Changes);

        project.Graphs[0].Graph.Nodes.First(node => node.Type == "literal.string").Properties["value"] = "Hi!";
        ProjectGraph function = project.AddFunction("Helper");
        ProjectGenerationResult second = project.PreviewGeneration(Compiler);

        Assert.Equal([GeneratedFileChangeKind.Modified, GeneratedFileChangeKind.Added], second.Changes.Select(change => change.Kind));
        Assert.Contains("+        Console.WriteLine(\"Hi!\");", second.Changes[0].UnifiedDiff("Generated/Program.g.cs"), StringComparison.Ordinal);

        project.Generate(Compiler);
        project.RemoveGraph(function.Graph);
        // Program stops being partial without functions, so its file changes too.
        IReadOnlyList<GeneratedFileChange> third = project.PreviewGeneration(Compiler).Changes;
        Assert.Equal([GeneratedFileChangeKind.Modified, GeneratedFileChangeKind.Deleted], third.Select(change => change.Kind));
        Assert.Equal(Path.Combine(project.GeneratedDirectory, "Helper.g.cs"), third[1].Path);
        Assert.True(File.Exists(third[1].Path));
    }

    [Fact]
    public void PreviewReportsOwnershipConflictsInsteadOfChanges()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        Directory.CreateDirectory(project.GeneratedDirectory);
        File.WriteAllText(Path.Combine(project.GeneratedDirectory, "Program.g.cs"), "namespace Hello; internal static class Mine { }\n");

        ProjectGenerationResult preview = project.PreviewGeneration(Compiler);

        Assert.False(preview.Success);
        Assert.Empty(preview.Changes);
    }

    [Fact]
    public void AddedSourceFileCompilesWithTheProjectAndIsNeverOverwritten()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");

        string path = project.AddSourceFile("Helpers");

        Assert.Equal("namespace Hello;\n\ninternal static class Helpers\n{\n}\n", File.ReadAllText(path));
        Assert.Contains(path, project.HandwrittenSources());
        Assert.True(project.Generate(Compiler).Success);
        Assert.Throws<IOException>(() => project.AddSourceFile("Helpers"));
        Assert.Throws<ArgumentException>(() => project.AddSourceFile("class"));
    }

    [Fact]
    public void UnsavedHandwrittenTextIsVerifiedInsteadOfTheFileOnDisk()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string path = project.AddSourceFile("Helpers");
        var unsaved = new Dictionary<string, string> { [path] = "namespace Hello;\ninternal static class Helpers { static int X = \"no\"; }\n" };

        ProjectGenerationResult preview = project.PreviewGeneration(Compiler, unsaved);

        GraphDiagnosticAssert(preview, path, line: 2);
        Assert.False(project.Generate(Compiler, unsaved).Success);
        Assert.False(Directory.Exists(project.GeneratedDirectory));
        Assert.True(project.PreviewGeneration(Compiler).Success);
    }

    private static void GraphDiagnosticAssert(ProjectGenerationResult result, string path, int line)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError && diagnostic.FilePath == path && diagnostic.Line == line);
    }

    [Fact]
    public void SavingSourceFilesProtectsGeneratedCode()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        project.Generate(Compiler);
        string path = project.AddSourceFile("Helpers");
        string generated = Path.Combine(project.GeneratedDirectory, "Program.g.cs");

        project.SaveSourceFile(path, "namespace Hello;\n");

        Assert.Equal("namespace Hello;\n", File.ReadAllText(path));
        Assert.Throws<InvalidOperationException>(() => project.SaveSourceFile(generated, "// mine\n"));
        Assert.Throws<InvalidOperationException>(() => project.SaveSourceFile(path, File.ReadAllText(generated)));
        Assert.Throws<ArgumentException>(() => project.SaveSourceFile(Path.Combine(directory.Path, "..", "Outside.cs"), ""));
        Assert.Throws<ArgumentException>(() => project.SaveSourceFile(Path.Combine(directory.Path, "notes.txt"), ""));
    }

    [Fact]
    public void DefinitionsInGeneratedCodeOpenTheirGraphAndNode()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        ProjectGraph greet = project.AddFunction("Greet");
        string path = project.AddSourceFile("Helpers");
        const string text = "namespace Hello;\n\ninternal static class Helpers\n{\n    public static void Run() => Program.Greet();\n}\n";
        File.WriteAllText(path, text);

        ProjectDefinition? definition = project.FindDefinition(Compiler, path, text.IndexOf("Greet", StringComparison.Ordinal) + 2);

        Assert.NotNull(definition);
        Assert.Same(greet, definition.Graph);
        Assert.Equal(project.GeneratedPath(greet.Graph), definition.FilePath);
        Assert.Equal("Program.Greet()", definition.SymbolName);
    }

    [Fact]
    public void DefinitionsInHandwrittenCodeAndOfTheFrameworkAreFound()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string path = project.AddSourceFile("Helpers");
        const string text = "namespace Hello;\n\ninternal static class Helpers\n{\n    public static int Twice(int value) => value * 2;\n\n    public static int Four() => Twice(2);\n}\n";
        File.WriteAllText(path, text);
        string generated = project.GeneratedPath(project.Graphs[0].Graph);
        string program = project.CompileGraphs(Compiler).Sources[0].Text;

        ProjectDefinition? twice = project.FindDefinition(Compiler, path, text.LastIndexOf("Twice", StringComparison.Ordinal));
        ProjectDefinition? writeLine = project.FindDefinition(Compiler, generated, program.IndexOf("WriteLine", StringComparison.Ordinal));
        ProjectDefinition? afterName = project.FindDefinition(Compiler, path, text.LastIndexOf("Twice", StringComparison.Ordinal) + "Twice".Length);

        Assert.Equal(new ProjectDefinition("Helpers.Twice(int)", path, 5, 23, null, null), twice);
        Assert.Equal(twice, afterName);
        Assert.NotNull(writeLine);
        Assert.Null(writeLine.FilePath);
        Assert.Null(project.FindDefinition(Compiler, path, text.IndexOf(';', StringComparison.Ordinal) + 2));
    }

    [Fact]
    public void StartupHookCompilesOnceIntoTheGivenFolder()
    {
        using var directory = new TemporaryDirectory();

        string path = DebugStartupHook.EnsureBuilt(directory.Path);

        Assert.True(File.Exists(path));
        Assert.Equal(path, DebugStartupHook.EnsureBuilt(directory.Path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }
}
