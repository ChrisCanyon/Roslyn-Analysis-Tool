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

        return CallGraphPruneSupport.BuildPrunedGraph(source, keep);
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
}
