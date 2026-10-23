using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ScriptArt.GraphModel;

/// <summary>One step of a file format migration: what it changes, and how, to reach <see cref="ToVersion"/>.</summary>
public sealed record SchemaMigration(int ToVersion, string Description, Action<JsonObject> Apply);

/// <summary>
/// Reads and writes graph documents in the versioned JSON format described in docs/GRAPH_FORMAT.md.
/// </summary>
public static class GraphSerializer
{
    public const string FileExtension = ".graph.json";

    /// <summary>
    /// The steps that bring a graph file from one schema version to the next, in order; step <c>n</c> converts version
    /// <c>n - 1</c> to <c>n</c>. Each changes the JSON object in place. Every version after 1 has exactly one step.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new(2, "adds comments", root => root["comments"] = new JsonArray()),
        new(3, "allows reroute points on wires; nothing to convert", _ => { }),
        new(4, "adds local variables", root => root["variables"] = new JsonArray()),
        new(5, "adds function parameters and return types", root => root["parameters"] = new JsonArray()),
        new(6, "adds classes and structs with properties", root => root["properties"] = new JsonArray()),
        new(7, "allows methods owned by a class or struct; existing functions stay in the program class", _ => { }),
        new(8, "adds constructors and type parameters", root => root["typeParameters"] = new JsonArray()),
    ];

    public static string Serialize(GraphDocument graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, GraphJsonContext.Default.GraphDocument) + "\n";
    }

    public static GraphDocument Deserialize(string json)
    {
        JsonObject root = ParseRoot(json);
        int version = ReadSchemaVersion(root, GraphDocument.CurrentSchemaVersion, "graph");

        // Migration steps transform the JSON object one version at a time, up to CurrentSchemaVersion.
        foreach (SchemaMigration migration in Migrations.Where(migration => migration.ToVersion > version))
        {
            migration.Apply(root);
        }

        root["schemaVersion"] = GraphDocument.CurrentSchemaVersion;

        // An absent list means an empty one. Without this, the generated deserializer would set the list to null.
        foreach (string list in (string[])["nodes", "connections", "comments", "variables", "parameters", "properties", "typeParameters"])
        {
            root[list] ??= new JsonArray();
        }

        GraphDocument? graph;
        try
        {
            graph = root.Deserialize(GraphJsonContext.Default.GraphDocument);
        }
        catch (JsonException exception)
        {
            throw new GraphFormatException($"The graph file is not valid: {exception.Message}", exception);
        }

        if (graph is null || string.IsNullOrWhiteSpace(graph.Id))
        {
            throw new GraphFormatException("The graph file has no graph identifier.");
        }

        return graph;
    }

    public static GraphDocument Load(string path)
    {
        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch (GraphFormatException exception)
        {
            throw new GraphFormatException($"{path}: {exception.Message}", exception);
        }
    }

    public static void Save(string path, GraphDocument graph) => AtomicFile.WriteAllText(path, Serialize(graph));

    /// <summary>Parses a JSON document whose root must be an object.</summary>
    public static JsonObject ParseRoot(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new GraphFormatException("The file does not contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new GraphFormatException($"The file is not valid JSON: {exception.Message}", exception);
        }
    }

    /// <summary>Reads <c>schemaVersion</c> and rejects versions this build cannot understand.</summary>
    public static int ReadSchemaVersion(JsonObject root, int currentVersion, string documentKind)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["schemaVersion"] is not JsonValue value || !value.TryGetValue(out int version))
        {
            throw new GraphFormatException($"The {documentKind} file has no schemaVersion.");
        }

        if (version < 1)
        {
            throw new GraphFormatException($"The {documentKind} file has an invalid schemaVersion {version}.");
        }

        if (version > currentVersion)
        {
            throw new GraphFormatException(
                $"The {documentKind} file uses schemaVersion {version}, but this version of the IDE supports up to {currentVersion}. Update the IDE to open it.");
        }

        return version;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NewLine = "\n",
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(GraphDocument))]
internal sealed partial class GraphJsonContext : JsonSerializerContext;
