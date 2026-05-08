namespace GatewayCallGraph;

/// <summary>
/// "Hide these impl FQNs" — given a built graph and a set of node FQNs the
/// user wants out of view, produce a new graph with those nodes removed plus
/// any subtree that was only reachable through them.
///
/// View-time prune (mirrors <see cref="CallGraphFocus"/>) so the picker can
/// re-render without rebuilding via Roslyn. The walker stays oblivious to
/// the user's filter choices — it builds one canonical graph per (root,
/// expandPastBoundaries), and this transform peels off views.
///
/// Pruning rule:
///   1. Any node whose FQN is in <c>hiddenFqns</c> is removed.
///   2. After step 1, any node whose only path from a root has been cut is
///      also removed (orphan subtrees). Roots are nodes with indegree 0
///      after the hidden nodes are gone.
///
/// This means hiding e.g. <c>RestApiUtilityBillingGateway.GetReceivables</c>
/// also removes anything ONLY reached through it — repository calls inside
/// its body, helper methods called only by it, etc. Nodes still reachable
/// via other paths stay.
/// </summary>
public static class CallGraphHideImpls
{
    /// <summary>
    /// Returns the original graph unchanged when <paramref name="hiddenFqns"/>
    /// is null or empty (the common case — most requests don't hide anything).
    /// Otherwise produces a new pruned graph; original is left alone.
    /// </summary>
    public static CallGraph Prune(CallGraph source, IReadOnlySet<string>? hiddenFqns)
    {
        if (hiddenFqns == null || hiddenFqns.Count == 0) return source;

        // Step 1: identify the set of node ids to drop directly.
        var hiddenIds = new HashSet<int>();
        foreach (var n in source.Nodes)
        {
            if (hiddenFqns.Contains(n.Fqn)) hiddenIds.Add(n.Id);
        }
        if (hiddenIds.Count == 0) return source;

        // Step 2: orphan-subtree pruning. Walk forward from anything with
        // surviving inbound edges; everything we DON'T reach has lost its
        // only path and should be dropped too.
        //
        // Forward adjacency is built from edges that don't touch a hidden
        // endpoint — those edges are gone from the pruned graph, so they
        // can't carry reachability either.
        var forward = new Dictionary<int, List<int>>();
        var indegree = new Dictionary<int, int>();
        foreach (var n in source.Nodes)
        {
            if (hiddenIds.Contains(n.Id)) continue;
            indegree[n.Id] = 0;
        }
        foreach (var e in source.Edges)
        {
            if (hiddenIds.Contains(e.From) || hiddenIds.Contains(e.To)) continue;
            if (!forward.TryGetValue(e.From, out var list)) forward[e.From] = list = new List<int>();
            list.Add(e.To);
            indegree[e.To] = indegree.GetValueOrDefault(e.To) + 1;
        }

        // Reachable from any surviving root (indegree 0).
        var keep = new HashSet<int>();
        var stack = new Stack<int>();
        foreach (var (id, deg) in indegree)
        {
            if (deg == 0)
            {
                keep.Add(id);
                stack.Push(id);
            }
        }
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!forward.TryGetValue(cur, out var nexts)) continue;
            foreach (var n in nexts)
            {
                if (keep.Add(n)) stack.Push(n);
            }
        }

        // If hide-set + orphan-pruning didn't actually remove anything (e.g.
        // every "hidden" FQN was already absent and no orphans fell out),
        // return the original to skip the rebuild work and keep cache hits warm.
        if (keep.Count == source.Nodes.Count) return source;

        return CallGraphPruneSupport.BuildPrunedGraph(source, keep);
    }
}
