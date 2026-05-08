namespace GatewayCallGraph;

/// <summary>
/// Shared machinery for the post-pass graph→graph transforms (collapse,
/// hide-impls, focus, future ones). Each transform decides which node ids
/// to keep; this helper does the "rebuild a smaller graph carrying the same
/// metadata" work.
///
/// Lives separately because every transform was reimplementing the same
/// node-copy + edge-rewrite + id-remap loop. One place to fix bugs in.
/// </summary>
internal static class CallGraphPruneSupport
{
    /// <summary>
    /// Build a new <see cref="CallGraph"/> containing only the nodes whose
    /// ids are in <paramref name="keep"/>. Node kind, boundary tag, and
    /// (live) symbol carry over. Edges between two kept nodes are copied
    /// with their full annotation set unless <paramref name="keepEdges"/> is
    /// supplied — in that case only edges whose <c>(from, to, file, line, dispatch)</c>
    /// signature is in the set survive. Edges touching a dropped node are
    /// always skipped.
    ///
    /// <see cref="CallGraph.BoundaryCallCounts"/> is intentionally NOT copied
    /// — the polynomial answer differs on the pruned graph. Callers should
    /// recompute via <see cref="BoundaryCallCountAnalyzer.Compute"/> when
    /// the pruned graph is what they're going to render.
    /// </summary>
    public static CallGraph BuildPrunedGraph(
        CallGraph source,
        HashSet<int> keep,
        HashSet<(int from, int to, string? file, int line, bool dispatch)>? keepEdges = null)
    {
        var pruned = new CallGraph();
        var oldToNew = new Dictionary<int, int>();
        foreach (var n in source.Nodes)
        {
            if (!keep.Contains(n.Id)) continue;
            // Re-add via GetOrAddNode so the new graph's internal indexes
            // (Symbol -> id, fqn -> id) stay consistent. Symbol is still
            // populated on each node because pruning happens in-process
            // against the live build artifact.
            var newId = pruned.GetOrAddNode(n.Symbol);
            oldToNew[n.Id] = newId;

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
            if (keepEdges != null &&
                !keepEdges.Contains((e.From, e.To, e.CallSite.File, e.CallSite.Line, e.Dispatch)))
            {
                continue;
            }
            if (e.Dispatch)
            {
                pruned.AddDispatchEdge(nf, nt);
            }
            else
            {
                pruned.AddEdge(nf, nt, e.CallSite, e.Loop, e.Conditional);
            }
        }

        return pruned;
    }
}
