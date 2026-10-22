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
    public void AddedTypesAreSavedReloadedAndGenerated()
    {
        using var directory = new TemporaryDirectory();
        VisualProject created = VisualProject.Create(directory.Path, "Hello");
        ProjectGraph point = created.AddType("Point", GraphKind.Struct);
        point.Graph.Properties.Add(new GraphProperty { Id = "x", Name = "X", Type = "double", Value = "1.5" });
        Assert.Throws<IOException>(() => created.AddFunction("Point"));
        Assert.Throws<ArgumentException>(() => created.AddType("Shape", GraphKind.Function));
        created.Save();

        VisualProject loaded = VisualProject.Load(created.ProjectFilePath);
        ProjectGenerationResult result = loaded.Generate(Compiler);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(GraphKind.Struct, loaded.Graphs[1].Graph.Kind);
        Assert.Contains("public double X { get; set; } = 1.5;", File.ReadAllText(Path.Combine(directory.Path, "Generated", "Point.g.cs")), StringComparison.Ordinal);
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

    [Fact]
    public void ReferencesComeFromHintPathsAndRestoredPackages()
    {
        using var directory = new TemporaryDirectory();
        string csproj = Path.Combine(directory.Path, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Foo" Version="1.0.0" />
                <Reference Include="Local"><HintPath>lib\Local.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(directory.Path, "lib"));
        File.WriteAllText(Path.Combine(directory.Path, "lib", "Local.dll"), "");

        Assert.Equal((1, true), (ProjectReferences.Resolve(csproj).AssemblyPaths.Count, ProjectReferences.Resolve(csproj).PackagesNotRestored));

        string packages = Path.Combine(directory.Path, "packages");
        string foo = Path.Combine(packages, "foo", "1.0.0", "lib", "net8.0", "Foo.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(foo)!);
        File.WriteAllText(foo, "");
        Directory.CreateDirectory(Path.Combine(directory.Path, "obj"));
        File.WriteAllText(Path.Combine(directory.Path, "obj", "project.assets.json"), $$"""
            {
              "targets": {
                "net10.0": {
                  "Foo/1.0.0": { "type": "package", "compile": { "lib/net8.0/Foo.dll": {} } },
                  "Bar/2.0.0": { "type": "package", "compile": { "lib/net10.0/_._": {} } }
                }
              },
              "libraries": { "Foo/1.0.0": { "path": "foo/1.0.0" }, "Bar/2.0.0": { "path": "bar/2.0.0" } },
              "packageFolders": { {{System.Text.Json.JsonSerializer.Serialize(packages + Path.DirectorySeparatorChar)}}: {} }
            }
            """);

        ProjectReferences references = ProjectReferences.Resolve(csproj);

        Assert.False(references.PackagesNotRestored);
        Assert.Equal([Path.Combine(directory.Path, "lib", "Local.dll"), foo], references.AssemblyPaths.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RenamedGraphsMoveToTheirNewFilesOnSaveAndMethodsFollowTheirType()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Demo");
        ProjectGraph account = project.AddType("Account", GraphKind.Class);
        project.AddMethod(account.Graph, "Close");
        ProjectGraph helper = project.AddFunction("Helper");
        project.Save();

        account.Graph.Name = "Wallet";
        helper.Graph.Name = "Assist";
        project.Save();

        string graphs = Path.Combine(directory.Path, "Graphs");
        Assert.Equal(["Assist.graph.json", "Program.graph.json", "Wallet.Close.graph.json", "Wallet.graph.json"],
            Directory.GetFiles(graphs).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        VisualProject reloaded = VisualProject.Load(project.ProjectFilePath);
        Assert.Equal(["Graphs/Program.graph.json", "Graphs/Wallet.graph.json", "Graphs/Wallet.Close.graph.json", "Graphs/Assist.graph.json"],
            reloaded.Graphs.Select(graph => graph.RelativePath));
    }

    [Fact]
    public void RenameKeepsTheOldFileWhenTheNewNameIsInvalidOrTaken()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Demo");
        ProjectGraph first = project.AddFunction("First");
        project.AddFunction("Second");
        project.Save();

        first.Graph.Name = "Second";
        project.Save();
        first.Graph.Name = "not valid";
        project.Save();

        Assert.Equal("Graphs/First.graph.json", project.Graphs.Single(graph => graph.Graph == first.Graph).RelativePath);
        Assert.True(File.Exists(Path.Combine(directory.Path, "Graphs", "First.graph.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "Graphs", "Second.graph.json")));
    }

    [Fact]
    public void RemovingATypeRemovesItsMembersAndDeletesTheirFilesOnSave()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Demo");
        ProjectGraph point = project.AddType("Point", GraphKind.Struct);
        project.AddConstructor(point.Graph, "FromX");
        project.Save();

        IReadOnlyList<ProjectGraph> removed = project.RemoveGraph(point.Graph);
        project.Save();

        Assert.Equal(2, removed.Count);
        Assert.Equal(["Program.graph.json"], Directory.GetFiles(Path.Combine(directory.Path, "Graphs")).Select(Path.GetFileName));
        Assert.Equal(["Graphs/Program.graph.json"], VisualProject.Load(project.ProjectFilePath).Document.Graphs);
        Assert.Throws<InvalidOperationException>(() => project.RemoveGraph(project.Graphs[0].Graph));
    }
}
