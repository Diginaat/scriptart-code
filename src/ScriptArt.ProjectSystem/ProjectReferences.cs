using System.Text.Json;
using System.Xml.Linq;

namespace ScriptArt.ProjectSystem;

/// <summary>A NuGet package the project references; <paramref name="Version"/> is null when the item names none.</summary>
public sealed record PackageReferenceItem(string Id, string? Version);

/// <summary>
/// The assemblies a project references besides the framework: <c>&lt;Reference&gt;</c> items with a <c>HintPath</c>, the
/// compile-time assemblies of NuGet packages as recorded by restore in <c>obj/project.assets.json</c>, and the assemblies of
/// referenced projects once a Debug build has copied them to the project's output folder.
/// </summary>
/// <param name="AssemblyPaths">Full paths of the referenced assemblies that exist, in a stable order.</param>
/// <param name="PackagesNotRestored">True when the project has package references but restore has not run yet, so their assemblies are unknown.</param>
public sealed record ProjectReferences(IReadOnlyList<string> AssemblyPaths, bool PackagesNotRestored)
{
    /// <summary>True for a Godot 4 C# project (<c>Godot.NET.Sdk</c>), whose SDK brings the GodotSharp package without a <c>&lt;PackageReference&gt;</c>.</summary>
    public bool IsGodot { get; init; }

    /// <summary>Where restore records the packages' assemblies: <c>obj/project.assets.json</c>, under <c>.godot/mono/temp</c> for Godot.</summary>
    internal string AssetsFile { get; init; } = "";

    /// <summary>
    /// Full paths of the source generator and analyzer assemblies the packages bring (<c>analyzers/dotnet/cs</c>), such as
    /// Godot.SourceGenerators. Never loaded unless the user approved them (D-050).
    /// </summary>
    public IReadOnlyList<string> SourceGeneratorPaths { get; init; } = [];

    /// <summary>The <c>&lt;PackageReference&gt;</c> items, in file order.</summary>
    public IReadOnlyList<PackageReferenceItem> Packages { get; init; } = [];

    /// <summary>Full paths of the projects named by <c>&lt;ProjectReference&gt;</c> items, in file order.</summary>
    public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>
    /// True when the project's packages must be restored before their assemblies are known: it has package references and restore
    /// never ran, or the <c>.csproj</c> changed after the last restore (a package may have been added by hand or by another tool).
    /// </summary>
    public static bool NeedsRestore(string csprojPath)
    {
        ArgumentNullException.ThrowIfNull(csprojPath);
        ProjectReferences references = Resolve(csprojPath);
        if (references.Packages.Count == 0 && !references.IsGodot)
        {
            return false;
        }

        string assets = references.AssetsFile;
        return references.PackagesNotRestored || !File.Exists(assets) || File.GetLastWriteTimeUtc(csprojPath) > File.GetLastWriteTimeUtc(assets);
    }

    /// <summary>Reads the references of the project at <paramref name="csprojPath"/>. A missing or unreadable project has none.</summary>
    public static ProjectReferences Resolve(string csprojPath)
    {
        ArgumentNullException.ThrowIfNull(csprojPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        XDocument project;
        try
        {
            project = XDocument.Load(csprojPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new ProjectReferences([], PackagesNotRestored: false);
        }

        SortedSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement reference in project.Descendants().Where(element => element.Name.LocalName == "Reference"))
        {
            if (reference.Elements().FirstOrDefault(element => element.Name.LocalName == "HintPath")?.Value is { Length: > 0 } hint)
            {
                paths.Add(Path.GetFullPath(Path.Combine(directory, hint.Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        PackageReferenceItem[] packages = [.. project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference" && Attribute(element, "Include") is not null)
            .Select(element => new PackageReferenceItem(Attribute(element, "Include")!, Attribute(element, "Version")))];
        string[] projects = [.. project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference" && Attribute(element, "Include") is not null)
            .Select(element => Path.GetFullPath(Path.Combine(directory, Attribute(element, "Include")!.Replace('\\', Path.DirectorySeparatorChar))))];

        // A build copies each referenced project's assembly next to the project's own.
        string framework = project.Descendants().FirstOrDefault(element => element.Name.LocalName == "TargetFramework")?.Value.Trim() is { Length: > 0 } target
            ? target
            : "net10.0";
        foreach (string referenced in projects)
        {
            paths.Add(Path.Combine(directory, "bin", "Debug", framework, AssemblyName(referenced) + ".dll"));
        }

        // Godot.NET.Sdk references GodotSharp itself and keeps restore output under .godot/mono/temp/obj (D-047).
        bool godot = project.Root?.Attribute("Sdk")?.Value.StartsWith("Godot.NET.Sdk", StringComparison.OrdinalIgnoreCase) == true;
        bool hasPackages = packages.Length > 0 || godot;
        string assets = Path.Combine(directory, godot ? Path.Combine(".godot", "mono", "temp", "obj") : "obj", "project.assets.json");
        bool restored = File.Exists(assets);
        string[] generators = [];
        if (restored)
        {
            try
            {
                string assetsJson = File.ReadAllText(assets);
                paths.UnionWith(PackageCompileAssemblies(assetsJson));
                generators = [.. PackageSourceGenerators(assetsJson)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                restored = false;
            }
        }

        return new ProjectReferences([.. paths.Where(File.Exists)], hasPackages && !restored) { Packages = packages, Projects = projects, IsGodot = godot, AssetsFile = assets, SourceGeneratorPaths = generators };
    }

    // An item's attribute, or its child element of that name as older project files write it.
    private static string? Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    // The assembly a project builds: its <AssemblyName>, or the project file's name.
    private static string AssemblyName(string csprojPath)
    {
        try
        {
            if (XDocument.Load(csprojPath).Descendants().FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value.Trim() is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // A missing or unreadable project builds nothing the IDE can find; its file name is the best guess.
        }

        return Path.GetFileNameWithoutExtension(csprojPath);
    }

    /// <summary>The C# analyzer assemblies of every package of the project's target in a <c>project.assets.json</c> document.</summary>
    internal static IEnumerable<string> PackageSourceGenerators(string assetsJson)
    {
        using JsonDocument document = JsonDocument.Parse(assetsJson);
        JsonElement root = document.RootElement;
        string[] packageFolders = root.TryGetProperty("packageFolders", out JsonElement folders)
            ? [.. folders.EnumerateObject().Select(folder => folder.Name)]
            : [];
        if (!root.TryGetProperty("targets", out JsonElement targets) || !root.TryGetProperty("libraries", out JsonElement libraries))
        {
            return [];
        }

        List<string> results = [];
        foreach (JsonProperty target in targets.EnumerateObject().Where(target => !target.Name.Contains('/', StringComparison.Ordinal)).Take(1))
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (!libraries.TryGetProperty(library.Name, out JsonElement description)
                    || !description.TryGetProperty("type", out JsonElement type) || type.GetString() != "package"
                    || !description.TryGetProperty("path", out JsonElement libraryPath)
                    || !description.TryGetProperty("files", out JsonElement files))
                {
                    continue;
                }

                foreach (string file in files.EnumerateArray().Select(file => file.GetString() ?? "")
                    .Where(file => file.StartsWith("analyzers/dotnet/cs/", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    string relative = Path.Combine(libraryPath.GetString()!, file).Replace('/', Path.DirectorySeparatorChar);
                    results.AddRange(packageFolders.Select(folder => Path.Combine(folder, relative)).Where(File.Exists).Take(1));
                }
            }
        }

        return results;
    }

    /// <summary>The compile-time assemblies of every package in a <c>project.assets.json</c> document.</summary>
    internal static IEnumerable<string> PackageCompileAssemblies(string assetsJson)
    {
        using JsonDocument document = JsonDocument.Parse(assetsJson);
        JsonElement root = document.RootElement;
        string[] packageFolders = root.TryGetProperty("packageFolders", out JsonElement folders)
            ? [.. folders.EnumerateObject().Select(folder => folder.Name)]
            : [];
        if (!root.TryGetProperty("targets", out JsonElement targets) || !root.TryGetProperty("libraries", out JsonElement libraries))
        {
            return [];
        }

        List<string> results = [];

        // A project restored for one target framework has one target without a runtime identifier.
        foreach (JsonProperty target in targets.EnumerateObject().Where(target => !target.Name.Contains('/', StringComparison.Ordinal)).Take(1))
        {
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if ((library.Value.TryGetProperty("type", out JsonElement type) && type.GetString() != "package")
                    || !library.Value.TryGetProperty("compile", out JsonElement compile)
                    || !libraries.TryGetProperty(library.Name, out JsonElement description)
                    || !description.TryGetProperty("path", out JsonElement libraryPath))
                {
                    continue;
                }

                foreach (JsonProperty asset in compile.EnumerateObject())
                {
                    // "_._" marks a package that contributes no assembly for this framework.
                    if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = Path.Combine(libraryPath.GetString()!, asset.Name).Replace('/', Path.DirectorySeparatorChar);
                        results.AddRange(packageFolders.Select(folder => Path.Combine(folder, relative)).Where(File.Exists).Take(1));
                    }
                }
            }
        }

        return results;
    }
}
