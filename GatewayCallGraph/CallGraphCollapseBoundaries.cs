namespace GatewayCallGraph;

/// <summary>
/// "Stop at boundary" view — given a maximal call graph (the walker no
/// longer terminates at boundary nodes), keep boundary nodes themselves but
/// drop everything strictly downstream of them. Equivalent to the old
/// <c>expandPastBoundaries=false</c> behavior, but as a render-time prune
/// instead of a walker parameter.
///
/// Why a post-pass: every other view mode (hide impls, focus) is already a
/// graph→graph transform. Collapse-at-boundary now joins them, so the
/// build cache holds one canonical maximal graph per root method, and
/// every view toggle (expand on/off, hide impls, focus) is a millisecond
/// post-pass against that cached graph. No more 2× cache space for the
/// expand-on/off split, no more rebuilds when toggling.
///
/// Pruning rule:
///   1. Drop every node whose only paths from the root pass through some
///      boundary node before reaching it.
///   2. Boundary nodes themselves stay (they're the visible leaves).
///   3. Nodes with at least one root-path that doesn't cross a boundary
///      stay (diamond patterns where a method is reachable both via the
///      root directly AND via a boundary).
///   4. Outbound edges FROM boundary nodes are also dropped, even when
///      both endpoints survive via other paths. The boundary is a leaf;
///      what's inside its body shouldn't be visible at all.
/// </summary>
public static class CallGraphCollapseBoundaries
{
    /// <summary>
    /// Returns the original graph unchanged when no nodes are tagged as
    /// boundaries (nothing to collapse). Otherwise produces a new pruned
    /// graph; original is left alone.
    /// </summary>
    public static CallGraph Prune(CallGraph source)
    {
        var hasAnyBoundary = false;
        foreach (var n in source.Nodes)
        {
            if (n.BoundaryName != null) { hasAnyBoundary = true; break; }
        }
        if (!hasAnyBoundary) return source;

        // Identify true roots in the maximal graph — indegree 0 over ALL
        // edges. We need the real graph topology to figure out where to
        // start the walk; ignoring boundary outbound edges here (the bug
        // I had previously) makes deep boundary-only-reachable nodes look
        // root-like, which is the opposite of what we want.
        var indegree = new int[source.Nodes.Count];
        var forward = new Dictionary<int, List<int>>();
        foreach (var e in source.Edges)
        {
            indegree[e.To]++;
            if (!forward.TryGetValue(e.From, out var list)) forward[e.From] = list = new List<int>();
            list.Add(e.To);
        }

        var keep = new HashSet<int>();
        var stack = new Stack<int>();
        for (int i = 0; i < source.Nodes.Count; i++)
        {
            if (indegree[i] == 0)
            {
                keep.Add(i);
                stack.Push(i);
            }
        }
        // BFS forward, but treat boundary nodes as SINKS at traversal time —
        // visit them (they stay in keep), don't traverse out of them. Any
        // node only reachable via a boundary's outbound edges falls out of
        // keep naturally because nothing else pushes it onto the stack.
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (source.Nodes[cur].BoundaryName != null) continue;
            if (!forward.TryGetValue(cur, out var nexts)) continue;
            foreach (var n in nexts)
            {
                if (keep.Add(n)) stack.Push(n);
            }
        }

        // Drop boundary-outbound edges when copying. Even if both endpoints
        // survived (because the target is also reachable via a non-boundary
        // path), we don't want to draw the "what happens inside the boundary"
        // edge at all in the collapsed view — the boundary is a leaf.
        var keepEdgeKeys = new HashSet<(int from, int to, string? file, int line, bool dispatch)>();
        foreach (var e in source.Edges)
        {
            if (!keep.Contains(e.From) || !keep.Contains(e.To)) continue;
            if (source.Nodes[e.From].BoundaryName != null) continue;
            keepEdgeKeys.Add((e.From, e.To, e.CallSite.File, e.CallSite.Line, e.Dispatch));
        }
        if (keep.Count == source.Nodes.Count && keepEdgeKeys.Count == source.Edges.Count) return source;

        return CallGraphPruneSupport.BuildPrunedGraph(source, keep, keepEdgeKeys);
    }
}
