namespace GatewayCallGraph;

/// <summary>
/// "Focus on a node" — given a built graph rooted at some controller and a
/// target node FQN, produce a new graph containing only:
///   - every ancestor of the target (i.e. nodes on some path from root to target)
///   - the target itself
///   - every descendant of the target
/// Edges are kept iff both endpoints survive. Boundary tags, dispatch flags,
/// loop and conditional annotations all pass through unchanged.
///
/// This is a render-time prune; the caching layer keys on the focus FQN so
/// repeat focuses are cheap.
/// </summary>
public static class CallGraphFocus
{
    public static CallGraph? Prune(CallGraph source, string focusFqn)
    {
        var focus = source.Nodes.FirstOrDefault(n => n.Fqn == focusFqn);
        if (focus == null) return null;

        // Adjacency for forward (caller -> callee) and reverse traversal.
        var forward = new Dictionary<int, List<int>>();
        var reverse = new Dictionary<int, List<int>>();
        foreach (var e in source.Edges)
        {
            if (!forward.TryGetValue(e.From, out var f)) forward[e.From] = f = new List<int>();
            f.Add(e.To);
            if (!reverse.TryGetValue(e.To, out var r)) reverse[e.To] = r = new List<int>();
            r.Add(e.From);
        }

        var keep = new HashSet<int> { focus.Id };
        Walk(reverse, focus.Id, keep); // ancestors
        Walk(forward, focus.Id, keep); // descendants

        return BuildPrunedGraph(source, keep);
    }

    private static void Walk(Dictionary<int, List<int>> adj, int start, HashSet<int> keep)
    {
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!adj.TryGetValue(cur, out var nexts)) continue;
            foreach (var n in nexts)
            {
                if (keep.Add(n)) stack.Push(n);
            }
        }
    }

    private static CallGraph BuildPrunedGraph(CallGraph source, HashSet<int> keep)
    {
        // Re-issue node ids in the pruned graph so consumers don't see gaps.
        // Edges and BoundaryCallCounts get remapped to the new ids.
        var pruned = new CallGraph();
        var oldToNew = new Dictionary<int, int>();
        foreach (var n in source.Nodes)
        {
            if (!keep.Contains(n.Id)) continue;
            // Re-add via GetOrAddNode using the symbol so the new graph's
            // internal indexes (Symbol -> id, fqn -> id) stay consistent.
            var newId = pruned.GetOrAddNode(n.Symbol);
            oldToNew[n.Id] = newId;

            // Carry over kind + boundary tag.
            if (n.Kind != NodeKind.Intermediate) pruned.PromoteKind(newId, n.Kind);
            if (n.BoundaryName != null)
            {
                var cat = BoundarySeeds.GetByName(n.BoundaryName);
                if (cat != null) pruned.TagBoundary(newId, cat);
            }
        }

        foreach (var e in source.Edges)
        {
            if (!oldToNew.TryGetValue(e.From, out var nf) ||
                !oldToNew.TryGetValue(e.To, out var nt)) continue;
            if (e.Dispatch)
            {
                pruned.AddDispatchEdge(nf, nt);
            }
            else
            {
                pruned.AddEdge(nf, nt, e.CallSite, e.Loop, e.Conditional);
            }
        }

        // BoundaryCallCounts will be recomputed by the caller after pruning.
        return pruned;
    }
}
