using System.Text.Json;
using System.Text.Json.Serialization;
using VisualStudioLikeIDE.GraphModel;

namespace VisualStudioLikeIDE.ProjectSystem;

/// <summary>
/// The contents of a <c>.vside</c> file: project identity and the graphs it contains.
/// Build settings live in the ordinary SDK-style <c>.csproj</c>, which the user owns.
/// </summary>
public sealed class ProjectDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Project name; also the name of the <c>.csproj</c> and the assembly.</summary>
    public required string Name { get; set; }

    /// <summary>Namespace of generated types.</summary>
    public required string RootNamespace { get; set; }

    /// <summary>Graph files relative to the project directory, using '/' separators.</summary>
    public List<string> Graphs { get; init; } = [];
}

/// <summary>Reads and writes <see cref="ProjectDocument"/> files.</summary>
public static class ProjectSerializer
{
    public static string Serialize(ProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, ProjectJsonContext.Default.ProjectDocument) + "\n";
    }

    public static ProjectDocument Deserialize(string json)
    {
        var root = GraphSerializer.ParseRoot(json);
        GraphSerializer.ReadSchemaVersion(root, ProjectDocument.CurrentSchemaVersion, "project");
        try
        {
            return root.Deserialize(ProjectJsonContext.Default.ProjectDocument)
                ?? throw new GraphFormatException("The project file is empty.");
        }
        catch (JsonException exception)
        {
            throw new GraphFormatException($"The project file is not valid: {exception.Message}", exception);
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    NewLine = "\n")]
[JsonSerializable(typeof(ProjectDocument))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext;
