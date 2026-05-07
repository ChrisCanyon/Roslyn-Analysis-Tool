using System.Text.Json.Serialization;

namespace GatewayCallGraph;

/// <summary>
/// Path-aware call-count formula for a single boundary node, expressed as a
/// polynomial in N (loop iterations).
///
/// Mental model: each edge is a multiplier — plain edge = 1, edge inside one
/// loop = N, edge inside k nested loops = N^k. A path from the controller to
/// the boundary contributes the *product* of its edge multipliers. The total
/// for a boundary is the *sum* across all such paths.
///
/// Result is a polynomial in N: Coefficients[0] is the constant term,
/// Coefficients[k] is the coefficient of N^k. e.g. [5, 1, 2] means
/// "2N^2 + N + 5". Formula is the rendered string for convenience.
/// </summary>
public sealed record BoundaryCallCount(
    [property: JsonPropertyName("nodeId")] int NodeId,
    [property: JsonPropertyName("fqn")] string Fqn,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("coefficients")] IReadOnlyList<long> Coefficients,
    [property: JsonPropertyName("formula")] string Formula);

/// <summary>
/// Computes per-boundary call-count polynomials over the call graph.
///
/// Algorithm: dynamic programming on the DAG. For each node, the polynomial
/// representing "how many times this node is invoked when the controller is
/// invoked once" is:
///   poly(root) = 1
///   poly(node) = Σ over incoming edges (caller -> node):  poly(caller) · M(edge)
/// where M(plain) = 1, M(loop, depth k) = N^k, M(dispatch) = 1.
///
/// We process nodes in topological order via Kahn's algorithm. Single pass,
/// O(E + N·D) where D is the largest polynomial degree (= max nesting depth on
/// any path).
/// </summary>
public static class BoundaryCallCountAnalyzer
{
    public static List<BoundaryCallCount> Compute(CallGraph graph)
    {
        var polys = ComputeNodePolynomials(graph);
        var results = new List<BoundaryCallCount>();
        foreach (var node in graph.Nodes)
        {
            if (node.BoundaryName == null) continue;
            var coeffs = polys[node.Id];
            results.Add(new BoundaryCallCount(
                NodeId: node.Id,
                Fqn: node.Fqn,
                Category: node.BoundaryName,
                Coefficients: coeffs,
                Formula: Format(coeffs)));
        }
        // Sort alphabetically by FQN so methods on the same class/interface
        // cluster together in the report (e.g. all IPaymentGateway.* in
        // one block, all PaymentRepository.* in another). Ordinal so case is
        // stable across machines.
        results.Sort((a, b) => string.CompareOrdinal(a.Fqn, b.Fqn));
        return results;
    }

    private static long[][] ComputeNodePolynomials(CallGraph graph)
    {
        var n = graph.Nodes.Count;
        var polys = new long[n][];
        for (int i = 0; i < n; i++) polys[i] = Array.Empty<long>();

        var indegree = new int[n];
        var outgoing = new List<GraphEdge>[n];
        for (int i = 0; i < n; i++) outgoing[i] = new List<GraphEdge>();

        foreach (var e in graph.Edges)
        {
            indegree[e.To]++;
            outgoing[e.From].Add(e);
        }

        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (indegree[i] == 0)
            {
                polys[i] = new long[] { 1 };
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            foreach (var e in outgoing[u])
            {
                int shift = e.Dispatch ? 0 : (e.Loop != null ? Math.Max(1, e.Loop.Depth) : 0);
                var contribution = PolyShift(polys[u], shift);
                polys[e.To] = PolyAdd(polys[e.To], contribution);
                indegree[e.To]--;
                if (indegree[e.To] == 0) queue.Enqueue(e.To);
            }
        }

        return polys;
    }

    private static long[] PolyAdd(long[] a, long[] b)
    {
        var len = Math.Max(a.Length, b.Length);
        var outArr = new long[len];
        for (int i = 0; i < a.Length; i++) outArr[i] += a[i];
        for (int i = 0; i < b.Length; i++) outArr[i] += b[i];
        return outArr;
    }

    private static long[] PolyShift(long[] p, int k)
    {
        if (k == 0) return (long[])p.Clone();
        var outArr = new long[p.Length + k];
        for (int i = 0; i < p.Length; i++) outArr[i + k] = p[i];
        return outArr;
    }

    private static int MaxDegree(IReadOnlyList<long> p)
    {
        for (int i = p.Count - 1; i >= 0; i--)
            if (p[i] != 0) return i;
        return -1;
    }

    public static string Format(IReadOnlyList<long> p)
    {
        if (p == null || p.Count == 0) return "0";
        var pieces = new List<string>();
        for (int i = p.Count - 1; i >= 0; i--)
        {
            var c = p[i];
            if (c == 0) continue;
            pieces.Add(FormatTerm(c, i));
        }
        return pieces.Count == 0 ? "0" : string.Join(" + ", pieces);
    }

    private static string FormatTerm(long coef, int power)
    {
        if (power == 0) return coef.ToString();
        var variable = power == 1 ? "N" : $"N^{power}";
        return coef == 1 ? variable : $"{coef}{variable}";
    }
}
