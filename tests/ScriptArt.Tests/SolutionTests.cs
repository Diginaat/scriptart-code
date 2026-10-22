using ScriptArt.ProjectSystem;

namespace ScriptArt.Tests;

/// <summary>Package and project references read from the <c>.csproj</c>, and what solution management accepts.</summary>
public sealed class SolutionTests
{
    [Fact]
    public void PackagesAndProjectReferencesAreListed()
    {
        using var directory = new TemporaryDirectory();
        string app = Path.Combine(directory.Path, "App");
        string library = Path.Combine(directory.Path, "Library");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "Library.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Shapes</AssemblyName></PropertyGroup></Project>");
        string csproj = Path.Combine(app, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Humanizer" Version="2.14.1" />
                <PackageReference Include="Floating"><Version>1.*</Version></PackageReference>
                <ProjectReference Include="..\Library\Library.csproj" />
              </ItemGroup>
            </Project>
            """);
        string built = Path.Combine(app, "bin", "Debug", "net10.0", "Shapes.dll");

        ProjectReferences before = ProjectReferences.Resolve(csproj);
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllBytes(built, []);
        ProjectReferences after = ProjectReferences.Resolve(csproj);

        Assert.Equal([new PackageReferenceItem("Humanizer", "2.14.1"), new PackageReferenceItem("Floating", "1.*")], before.Packages);
        Assert.Equal([Path.Combine(library, "Library.csproj")], before.Projects);
        Assert.True(before.PackagesNotRestored);
        Assert.Empty(before.AssemblyPaths);
        Assert.Equal([built], after.AssemblyPaths);
    }

    [Theory]
    [InlineData("Newtonsoft.Json", true)]
    [InlineData("my_package-2", true)]
    [InlineData("--source", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void PackageIdsAreValidated(string id, bool valid) => Assert.Equal(valid, ProjectSolution.IsValidPackageId(id));

    [Theory]
    [InlineData("13.0.3", true)]
    [InlineData("2.0.0-preview.1", true)]
    [InlineData("13.*", true)]
    [InlineData("-1", false)]
    [InlineData("latest version", false)]
    public void VersionsAreValidated(string version, bool valid) => Assert.Equal(valid, ProjectSolution.IsValidVersion(version));

    [Fact]
    public async Task InvalidRequestsAreRefusedBeforeRunningDotnet()
    {
        using var directory = new TemporaryDirectory();
        VisualProject project = VisualProject.Create(directory.Path, "Hello");
        string inside = Path.Combine(directory.Path, "Inner", "Inner.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, "<Project />");
        File.WriteAllText(Path.Combine(directory.Path, "Hello.slnx"), "<Solution />");

        await Assert.ThrowsAsync<ArgumentException>(() => ProjectSolution.AddPackageAsync(project, "--source", null, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectSolution.AddPackageAsync(project, "Humanizer", "--prerelease", _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectSolution.AddProjectReferenceAsync(project, inside, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectSolution.AddProjectReferenceAsync(project, project.CsprojPath, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => ProjectSolution.CreateAsync(project, _ => { }, CancellationToken.None));
        Assert.Equal(Path.Combine(directory.Path, "Hello.slnx"), ProjectSolution.Find(project));
    }
}
