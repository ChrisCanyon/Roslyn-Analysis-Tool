using System.Text.Json;
using System.Text.Json.Serialization;

namespace GatewayCallGraph;

/// <summary>
/// Writes a CallGraph to disk as a single graph.json file. Schema:
/// {
///   "nodes": [{ "id", "fqn", "type", "method", "kind" }, ...],
///   "edges": [{ "from", "to", "callSite": { "file", "line" }, "loop": { "kind", "subkind", "file", "line" } | null }, ...]
/// }
/// </summary>
public static class GraphJsonWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static void Write(string outputPath, CallGraph graph)
    {
        var json = JsonSerializer.Serialize(graph, Options);
        File.WriteAllText(outputPath, json);
    }
}
