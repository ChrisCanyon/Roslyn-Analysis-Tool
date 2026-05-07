using System.Diagnostics;
using System.Text;

namespace GatewayCallGraph;

/// <summary>
/// Converts a CallGraph to Graphviz DOT, then optionally pipes the DOT through
/// `dot -Tsvg` to produce inline SVG markup. Styling rules:
///   - controller_action     -> filled green box
///   - boundary nodes        -> styled by their BoundaryCategory's NodeStyle
///                              (see <see cref="BoundarySeeds"/>)
///   - intermediate          -> white ellipse
///   - statement_loop edges  -> orange + label "<subkind> @line N"
///   - enumerable_loop edges -> purple dashed + label ".<subkind>() @line N"
///   - dispatch edges        -> grey dashed (interface -> implementation)
///   - normal edges          -> grey
/// </summary>
public static class GraphvizRenderer
{
    public static string ToDot(CallGraph graph)
    {
        // Index boundary categories by name for O(1) per-node style lookup.
        var stylesByName = graph.Boundaries.ToDictionary(b => b.Name, b => b.Style, StringComparer.Ordinal);

        // Pre-compute: for each node id, the set of boundary category names
        // reachable downstream (i.e. via outgoing edges, transitively, until a
        // boundary node is hit). Used to tint edges by where they lead.
        var reachable = ComputeReachableBoundaryCategories(graph);

        var sb = new StringBuilder();
        sb.AppendLine("digraph CallGraph {");
        // LR (left-to-right) keeps deep call chains readable left-across instead
        // of pushing siblings far apart horizontally. The other dependency-graph
        // views in this app use LR for the same reason.
        sb.AppendLine("  rankdir=\"LR\";");
        sb.AppendLine("  graph [splines=true, overlap=false, ranksep=\"0.7\", nodesep=\"0.25\", fontname=\"Segoe UI\", fontsize=10];");
        sb.AppendLine("  node  [fontname=\"Segoe UI\", fontsize=10];");
        sb.AppendLine("  edge  [fontname=\"Segoe UI\", fontsize=9];");
        sb.AppendLine();

        foreach (var node in graph.Nodes)
        {
            var attrs = NodeAttrs(node, stylesByName);
            sb.AppendLine($"  n{node.Id} [{FmtAttrs(attrs)}];");
        }
        sb.AppendLine();

        foreach (var edge in graph.Edges)
        {
            var attrs = EdgeAttrs(edge, reachable);
            sb.AppendLine($"  n{edge.From} -> n{edge.To} [{FmtAttrs(attrs)}];");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Reverse-BFS from every boundary node along the edge graph. Returns a
    /// dictionary: node id -> set of boundary category names reachable from
    /// that node (inclusive of itself if it's a boundary).
    ///
    /// We use this to color edges: an edge whose target leads to one boundary
    /// category is tinted that category's color; an edge whose target leads to
    /// multiple categories is tinted purple ("mixed downstream IO").
    /// </summary>
    private static Dictionary<int, HashSet<string>> ComputeReachableBoundaryCategories(CallGraph graph)
    {
        // Adjacency: for each node, the predecessors (callers) that reach it.
        var predecessors = new Dictionary<int, List<int>>();
        foreach (var edge in graph.Edges)
        {
            if (!predecessors.TryGetValue(edge.To, out var list))
                predecessors[edge.To] = list = new List<int>();
            list.Add(edge.From);
        }

        var reachable = new Dictionary<int, HashSet<string>>();
        foreach (var n in graph.Nodes) reachable[n.Id] = new HashSet<string>(StringComparer.Ordinal);

        // Seed from boundary nodes: each boundary contributes its category to
        // its own set, then propagates upstream.
        var queue = new Queue<int>();
        foreach (var n in graph.Nodes)
        {
            if (n.BoundaryName != null)
            {
                reachable[n.Id].Add(n.BoundaryName);
                queue.Enqueue(n.Id);
            }
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!predecessors.TryGetValue(cur, out var preds)) continue;
            foreach (var pred in preds)
            {
                var added = false;
                foreach (var cat in reachable[cur])
                {
                    if (reachable[pred].Add(cat)) added = true;
                }
                if (added) queue.Enqueue(pred);
            }
        }

        return reachable;
    }

    /// <summary>
    /// Renders a CallGraph straight to inline SVG markup by invoking `dot -Tsvg`.
    /// The Graphviz binary path can be overridden; defaults to the standard
    /// Windows install location and falls back to PATH if not found.
    /// </summary>
    public static async Task<string> RenderSvgAsync(CallGraph graph, string? dotExecutablePath = null)
    {
        var dot = ToDot(graph);
        var dotExe = dotExecutablePath ?? ResolveDotExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = dotExe,
            ArgumentList = { "-Tsvg" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start Graphviz at: {dotExe}");

        // Pipe DOT in, read SVG out. Use UTF-8 throughout — DOT and SVG are both
        // text and we want characters like generic angle-brackets to round-trip.
        await proc.StandardInput.WriteAsync(dot).ConfigureAwait(false);
        proc.StandardInput.Close();

        var svgTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().ConfigureAwait(false);

        var svg = await svgTask.ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Graphviz exited {proc.ExitCode}: {err}");
        }

        return svg;
    }

    private static string ResolveDotExecutable()
    {
        var standardPath = @"C:\Program Files\Graphviz\bin\dot.exe";
        if (File.Exists(standardPath)) return standardPath;
        return "dot"; // rely on PATH
    }

    private static Dictionary<string, string> NodeAttrs(GraphNode node, IReadOnlyDictionary<string, NodeStyleInfo> stylesByName)
    {
        var typeShort = node.TypeName.Length > 0
            ? node.TypeName[(node.TypeName.LastIndexOf('.') + 1)..]
            : "?";
        var label = $"{typeShort}.{node.MethodName}";
        var tooltip = node.Fqn;

        // Boundary tag wins over kind: a node that's both a controller action
        // AND a boundary should render as the boundary (boundaries are usually
        // visually more important — they're the leaves of the graph).
        if (node.BoundaryName != null && stylesByName.TryGetValue(node.BoundaryName, out var boundaryStyle))
        {
            return new()
            {
                ["label"] = label,
                ["tooltip"] = $"{tooltip}\n[boundary: {node.BoundaryName}]",
                ["shape"] = boundaryStyle.Shape,
                ["style"] = boundaryStyle.Style,
                ["fillcolor"] = boundaryStyle.FillColor,
                ["color"] = boundaryStyle.StrokeColor,
                ["fontcolor"] = boundaryStyle.FontColor,
            };
        }

        return node.Kind switch
        {
            NodeKind.ControllerAction => new()
            {
                ["label"] = label,
                ["tooltip"] = tooltip,
                ["shape"] = "box",
                ["style"] = "filled,bold",
                ["fillcolor"] = "#b6f2c1",
                ["color"] = "#2e7d32",
            },
            _ => new()
            {
                ["label"] = label,
                ["tooltip"] = tooltip,
                ["shape"] = "ellipse",
                ["style"] = "filled",
                ["fillcolor"] = "white",
                ["color"] = "#888888",
            },
        };
    }

    // Edge tints for "this path leads to an IO boundary." Loops still win — these
    // only apply to non-loop, non-dispatch edges. Mixed paths (DB + external in
    // the same downstream subtree) get purple to signal "both kinds of IO below."
    private const string LeadsToExternalColor = "#c62828"; // matches external_system_gateway stroke
    private const string LeadsToDatabaseColor = "#1565c0"; // matches database_query stroke
    private const string LeadsToMixedColor = "#6a1b9a";    // purple — both DB and external reachable

    private static Dictionary<string, string> EdgeAttrs(GraphEdge edge, Dictionary<int, HashSet<string>> reachable)
    {
        if (edge.Dispatch)
        {
            return new()
            {
                ["tooltip"] = "interface dispatch",
                ["color"] = "#9aa0a6",
                ["style"] = "dashed",
                ["arrowhead"] = "open",
            };
        }

        var callLine = edge.CallSite.Line;
        var baseTooltip = $"call @ line {callLine}";

        // Loop styling has priority — orange/purple loop coloring stays even if
        // the path also leads to IO. The loop is the more important visual.
        var loop = edge.Loop;
        if (loop != null)
        {
            var loopLine = loop.Line;
            var subkind = loop.Subkind;

            if (loop.Kind == LoopKind.StatementLoop)
            {
                return new()
                {
                    ["label"] = $"{subkind} @{loopLine}",
                    ["tooltip"] = $"{baseTooltip}; inside {subkind} at line {loopLine}",
                    ["color"] = "#ef6c00",
                    ["fontcolor"] = "#ef6c00",
                    ["penwidth"] = "2",
                };
            }

            if (loop.Kind == LoopKind.EnumerableLoop)
            {
                return new()
                {
                    ["label"] = $".{subkind}() @{loopLine}",
                    ["tooltip"] = $"{baseTooltip}; inside .{subkind}() at line {loopLine}",
                    ["color"] = "#6a1b9a",
                    ["fontcolor"] = "#6a1b9a",
                    ["penwidth"] = "2",
                    ["style"] = "dashed",
                };
            }

            return new()
            {
                ["label"] = $"loop @{loopLine}",
                ["tooltip"] = baseTooltip,
                ["color"] = "#ef6c00",
                ["penwidth"] = "2",
            };
        }

        // Non-loop, non-dispatch edge: tint by what's reachable downstream.
        // Look at the *target* node's reachable set — "if I traverse this edge,
        // do I end up at an IO boundary?"
        if (reachable.TryGetValue(edge.To, out var cats) && cats.Count > 0)
        {
            var hasExternal = cats.Contains(IoPrimitives.ExternalSystemGateway);
            var hasDb = cats.Contains(IoPrimitives.DatabaseQuery);
            string color;
            string suffix;
            if (hasExternal && hasDb)
            {
                color = LeadsToMixedColor;
                suffix = "leads to external + database";
            }
            else if (hasExternal)
            {
                color = LeadsToExternalColor;
                suffix = "leads to external";
            }
            else if (hasDb)
            {
                color = LeadsToDatabaseColor;
                suffix = "leads to database";
            }
            else
            {
                // Some other boundary category we don't have a special color for —
                // fall through to neutral grey rather than guessing.
                return new()
                {
                    ["tooltip"] = baseTooltip,
                    ["color"] = "#444444",
                };
            }
            return new()
            {
                ["tooltip"] = $"{baseTooltip}; {suffix}",
                ["color"] = color,
            };
        }

        return new()
        {
            ["tooltip"] = baseTooltip,
            ["color"] = "#444444",
        };
    }

    private static string FmtAttrs(Dictionary<string, string> attrs)
    {
        return string.Join(", ", attrs.Select(kv => $"{kv.Key}=\"{Escape(kv.Value)}\""));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
