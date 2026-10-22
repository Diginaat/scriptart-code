using System.Text.RegularExpressions;

namespace ScriptArt.ProjectSystem;

/// <summary>
/// Solution and reference management for a visual project, through the standard <c>dotnet</c> commands (<c>dotnet new sln</c>,
/// <c>dotnet sln add</c>, <c>dotnet add package</c>, <c>dotnet add reference</c>, and their <c>remove</c> forms). The <c>.csproj</c>
/// and the solution belong to the user; these change them only when the user asks, the way the user would from a terminal.
/// </summary>
public static partial class ProjectSolution
{
    /// <summary>
    /// The solution in the project directory: <c>{Name}.slnx</c> or <c>{Name}.sln</c> when present, otherwise the only
    /// <c>.slnx</c> or <c>.sln</c> there; null when there is none, or several and none named after the project.
    /// </summary>
    public static string? Find(VisualProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (string extension in new[] { ".slnx", ".sln" })
        {
            string named = Path.Combine(project.DirectoryPath, project.Name + extension);
            if (File.Exists(named))
            {
                return named;
            }
        }

        string[] solutions = [.. Directory.EnumerateFiles(project.DirectoryPath, "*.sln*")
            .Where(path => Path.GetExtension(path) is ".sln" or ".slnx")];
        return solutions.Length == 1 ? solutions[0] : null;
    }

    /// <summary>
    /// Creates <c>{Name}.slnx</c> in the project directory with the project in it, so the project opens in Visual Studio, Rider, or
    /// VS Code. Refuses when the directory already has a solution. Returns the exit code of the first command that failed, or 0.
    /// </summary>
    public static async Task<int> CreateAsync(VisualProject project, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Find(project) is { } existing)
        {
            throw new IOException($"'{Path.GetFileName(existing)}' already exists.");
        }

        int exitCode = await DotNetCli.RunAsync(
            ["new", "sln", "--format", "slnx", "--name", project.Name, "--output", project.DirectoryPath], output, cancellationToken).ConfigureAwait(false);
        return exitCode != 0
            ? exitCode
            : await DotNetCli.RunAsync(["sln", Path.Combine(project.DirectoryPath, project.Name + ".slnx"), "add", project.CsprojPath], output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// References the NuGet package <paramref name="packageId"/>, at <paramref name="version"/> or the latest stable version when
    /// null, and restores it, so its methods become nodes. Refuses an id or version NuGet would not accept.
    /// </summary>
    public static Task<int> AddPackageAsync(VisualProject project, string packageId, string? version, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsValidPackageId(packageId))
        {
            throw new ArgumentException($"'{packageId}' is not a NuGet package id. Use letters, digits, dots, hyphens, and underscores.", nameof(packageId));
        }

        if (version is not null && !IsValidVersion(version))
        {
            throw new ArgumentException($"'{version}' is not a package version, such as 13.0.3 or 2.0.0-preview.1.", nameof(version));
        }

        return DotNetCli.RunAsync(
            ["add", project.CsprojPath, "package", packageId, .. version is null ? Array.Empty<string>() : ["--version", version]], output, cancellationToken);
    }

    /// <summary>Removes the reference to the NuGet package <paramref name="packageId"/>.</summary>
    public static Task<int> RemovePackageAsync(VisualProject project, string packageId, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!IsValidPackageId(packageId))
        {
            throw new ArgumentException($"'{packageId}' is not a NuGet package id.", nameof(packageId));
        }

        return DotNetCli.RunAsync(["remove", project.CsprojPath, "package", packageId], output, cancellationToken);
    }

    /// <summary>
    /// References the project <paramref name="csprojPath"/> and adds it to the solution, if there is one. Its public methods become
    /// nodes after the next build. Refuses a project inside this project's directory, whose files this project would also compile.
    /// </summary>
    public static async Task<int> AddProjectReferenceAsync(VisualProject project, string csprojPath, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        string referenced = Path.GetFullPath(csprojPath);
        if (!referenced.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(referenced))
        {
            throw new ArgumentException($"'{csprojPath}' is not a C# project file.", nameof(csprojPath));
        }

        if (string.Equals(referenced, project.CsprojPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A project cannot reference itself.", nameof(csprojPath));
        }

        if (referenced.StartsWith(project.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(referenced)}' is inside the project folder, so its files would be compiled into {project.Name} too. Move it next to the project folder.",
                nameof(csprojPath));
        }

        int exitCode = await DotNetCli.RunAsync(["add", project.CsprojPath, "reference", referenced], output, cancellationToken).ConfigureAwait(false);
        return exitCode != 0 || Find(project) is not { } solution
            ? exitCode
            : await DotNetCli.RunAsync(["sln", solution, "add", referenced], output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the reference to the project <paramref name="csprojPath"/>. It stays in the solution.</summary>
    public static Task<int> RemoveProjectReferenceAsync(VisualProject project, string csprojPath, Action<string> output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        return DotNetCli.RunAsync(["remove", project.CsprojPath, "reference", Path.GetFullPath(csprojPath)], output, cancellationToken);
    }

    /// <summary>True for a NuGet package id: letters, digits, '.', '-', and '_', starting with a letter, digit, or '_'.</summary>
    public static bool IsValidPackageId(string? packageId) => packageId is { Length: > 0 and <= 100 } && PackageIdPattern().IsMatch(packageId);

    /// <summary>True for a NuGet version such as <c>13.0.3</c>, <c>2.0.0-preview.1</c>, or a floating version such as <c>13.*</c>.</summary>
    public static bool IsValidVersion(string? version) => version is { Length: > 0 and <= 64 } && VersionPattern().IsMatch(version);

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[0-9*][0-9A-Za-z.+\-*]*$")]
    private static partial Regex VersionPattern();
}
