using VisualStudioLikeIDE.Compiler;
using VisualStudioLikeIDE.Compiler.Nodes;
using VisualStudioLikeIDE.GraphModel;
using VisualStudioLikeIDE.ProjectSystem;

namespace VisualStudioLikeIDE.Tests;

public sealed class ProjectSystemTests
{
    private static readonly GraphCompiler Compiler = new(NodeLibrary.CreateDefault());

    [Fact]
    public void NewProjectHasStandardLayoutAndGenerates()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");

        Assert.True(File.Exists(Path.Combine(directory.Path, "Hello.vside")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "Hello.csproj")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "Graphs", "Program.graph.json")));

        ProjectGenerationResult result = project.Generate(Compiler);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        string generated = File.ReadAllText(Path.Combine(directory.Path, "Generated", "Program.g.cs"));
        Assert.Contains("Console.WriteLine(\"Hello, World!\");", generated, StringComparison.Ordinal);
        Assert.Contains("namespace Hello;", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRefusesToOverwriteExistingProject()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "Hello.csproj"), "<Project />");
        Assert.Throws<IOException>(() => VisualProject.Create(directory.Path, "Hello"));
        Assert.Equal("<Project />", File.ReadAllText(Path.Combine(directory.Path, "Hello.csproj")));
    }

    [Fact]
    public void ProjectReloadsFromDisk()
    {
        using var directory = new TemporaryDirectory();
        VisualProject created = VisualProject.Create(directory.Path, "Hello");
        VisualProject loaded = VisualProject.Load(created.ProjectFilePath);

        Assert.Equal("Hello", loaded.Name);
        ProjectGraph graph = Assert.Single(loaded.Graphs);
        Assert.Equal(3, graph.Graph.Nodes.Count);
    }

    [Fact]
    public void GenerationNeverOverwritesHandwrittenFile()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string target = Path.Combine(project.GeneratedDirectory, "Program.g.cs");
        Directory.CreateDirectory(project.GeneratedDirectory);
        File.WriteAllText(target, "// my own code\n");

        ProjectGenerationResult result = project.Generate(Compiler);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == GeneratedFileWriter.OwnershipConflictId);
        Assert.Equal("// my own code\n", File.ReadAllText(target));
    }

    [Fact]
    public void StaleGeneratedFilesAreRemovedButHandwrittenFilesStay()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        Assert.True(project.Generate(Compiler).Success);

        string stale = Path.Combine(project.GeneratedDirectory, "Old.g.cs");
        File.Copy(Path.Combine(project.GeneratedDirectory, "Program.g.cs"), stale);
        string handwritten = Path.Combine(project.GeneratedDirectory, "Notes.cs");
        File.WriteAllText(handwritten, "namespace Hello; internal static class Notes { }\n");

        ProjectGenerationResult result = project.Generate(Compiler);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(handwritten));
    }

    [Fact]
    public void UnchangedGenerationWritesNothing()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        Assert.NotEmpty(project.Generate(Compiler).ChangedFiles);
        Assert.Empty(project.Generate(Compiler).ChangedFiles);
    }

    [Fact]
    public void RoslynErrorsInHandwrittenCodeBlockGeneration()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        File.WriteAllText(Path.Combine(directory.Path, "Broken.cs"), "namespace Hello; internal static class Broken { static void M() { int x = \"no\"; } }\n");

        ProjectGenerationResult result = project.Generate(Compiler);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CS0029" && diagnostic.FilePath!.EndsWith("Broken.cs", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(project.GeneratedDirectory, "Program.g.cs")));
    }

    [Fact]
    public void GraphPathOutsideProjectIsRejected()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        project.Document.Graphs[0] = "../escape.graph.json";
        File.WriteAllText(project.ProjectFilePath, ProjectSerializer.Serialize(project.Document));

        Assert.Throws<GraphFormatException>(() => VisualProject.Load(project.ProjectFilePath));
    }
}
