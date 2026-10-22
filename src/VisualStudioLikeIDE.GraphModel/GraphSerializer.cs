using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VisualStudioLikeIDE.GraphModel;

/// <summary>
/// Reads and writes graph documents in the versioned JSON format described in docs/GRAPH_FORMAT.md.
/// </summary>
public static class GraphSerializer
{
    public const string FileExtension = ".graph.json";

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
        if (version < 2)
        {
            // Version 2 added comments; older files simply have none.
            root["comments"] = new JsonArray();
        }

        // Version 3 added optional reroute points on connections; older files have none, so there is nothing to convert.

        if (version < 4)
        {
            root["variables"] = new JsonArray();
        }

        if (version < 5)
        {
            root["parameters"] = new JsonArray();
        }

        root["schemaVersion"] = GraphDocument.CurrentSchemaVersion;

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
