using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace GatewayCallGraph;

/// <summary>
/// Post-build pass that annotates each dispatch edge with the concrete
/// classes whose vtable resolves through it (<see cref="GraphEdge.ServesTypes"/>).
///
/// Why a post-pass: the walker emits dispatch edges as it discovers them,
/// but the question "which concrete classes route through this impl?" is a
/// graph-shape question that's easier to answer after the maximal graph
/// exists. Same architectural pattern as <see cref="CallGraphCollapseBoundaries"/>
/// and the per-boundary polynomial counter — read the finished graph,
/// produce annotations.
///
/// What the picker uses this for:
///   - "Munis OVERRIDES GetReceivables": the dispatch edge IUBG.GetReceivables
///     -> Munis.GetReceivables has ServesTypes = [Munis].
///   - "Munis INHERITS GetAccountAlerts via RestApi": the dispatch edge
///     IUBG.GetAccountAlerts -> RestApi.GetAccountAlerts has
///     ServesTypes = [RestApi, Munis] — Munis's vtable resolves THROUGH
///     this base impl, so picking Munis still keeps the edge visible.
///
/// The picker filter rule then becomes "drop dispatch edges whose ServesTypes
/// doesn't contain the picked concrete class" — automatically correct for
/// both override and inheritance cases.
/// </summary>
public static class DispatchServesTypesResolver
{
    public static async Task ResolveAsync(CallGraph graph, Solution solution)
    {
        // Group dispatch edges by the from-node (interface or virtual base
        // method). For each from-node, walk every concrete in-source class
        // implementing that interface / extending that base, ask Roslyn
        // which impl method its vtable resolves to, and record the
        // concrete-class FQN against the resolved dispatch edge.
        var dispatchByFrom = new Dictionary<int, List<GraphEdge>>();
        foreach (var e in graph.Edges)
        {
            if (!e.Dispatch) continue;
            if (!dispatchByFrom.TryGetValue(e.From, out var list))
                dispatchByFrom[e.From] = list = new List<GraphEdge>();
            list.Add(e);
        }
        if (dispatchByFrom.Count == 0) return;

        // Index all node FQNs once so we can map "method that vtable resolves
        // to" back to a graph node id quickly. Nodes are FQN-keyed already
        // by the build path, so this is a direct lookup.
        var nodeIdByFqn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in graph.Nodes) nodeIdByFqn[n.Fqn] = n.Id;

        // Per-controller picker snapshot: concrete classes that can serve any
        // dispatch site of a given interface or virtual-base type. Aggregated
        // across all dispatch-source methods of the same containing type so
        // the picker offers ONE row per interface, not one per method. Stable
        // across post-pass prunes — see CallGraph.InterfaceImpls.
        var concretesByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (fromId, edges) in dispatchByFrom)
        {
            var fromNode = graph.Nodes[fromId];
            var fromMethod = fromNode.Symbol;
            var fromType = fromMethod?.ContainingType;
            if (fromMethod == null || fromType == null) continue;

            // Collect concrete classes whose vtable could route through this
            // dispatch site. Two cases:
            //   - Interface: every concrete class implementing the interface,
            //     directly or via inheritance. FindImplementationsAsync gives
            //     direct implementers; FindDerivedClassesAsync transitively
            //     covers anyone who inherits the implementation.
            //   - Virtual class method: the from-type itself (if concrete)
            //     plus all derived classes.
            var concretes = await FindRelevantConcreteClassesAsync(fromType, solution).ConfigureAwait(false);
            if (concretes.Count == 0) continue;

            // Map from "resolved impl method FQN" to "concrete classes that
            // resolve there." We collect across all concretes, then attribute
            // back onto the dispatch edge whose To-node matches that FQN.
            var typesByResolvedImplFqn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var concrete in concretes)
            {
                var resolvedMethod = ResolveImplFor(concrete, fromMethod);
                if (resolvedMethod == null) continue;
                var resolvedFqn = resolvedMethod.OriginalDefinition.ToDisplayString();
                if (!typesByResolvedImplFqn.TryGetValue(resolvedFqn, out var list))
                    typesByResolvedImplFqn[resolvedFqn] = list = new List<string>();
                list.Add(concrete.ToDisplayString());
            }

            // For each dispatch edge out of this from-node, look up its
            // To-node's FQN against the typesByResolvedImplFqn map and write
            // the result onto the edge.
            foreach (var edge in edges)
            {
                var toFqn = graph.Nodes[edge.To].Fqn;
                if (!typesByResolvedImplFqn.TryGetValue(toFqn, out var serves)) continue;
                graph.SetDispatchServesTypes(edge.From, edge.To, serves);
            }

            // Roll the concrete set up into the per-type picker snapshot.
            // The same interface can have multiple dispatch-source methods
            // in this graph (e.g. IUtilityBillingGateway.FindAccount and
            // IUtilityBillingGateway.GetReceivables); both contribute their
            // concrete classes to the same picker row.
            var fromTypeFqn = fromType.OriginalDefinition.ToDisplayString();
            if (!concretesByType.TryGetValue(fromTypeFqn, out var typeSet))
                concretesByType[fromTypeFqn] = typeSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in concretes) typeSet.Add(c.ToDisplayString());
        }

        // Emit the per-controller picker snapshot. Stable order so the
        // dropdown rows render predictably across requests.
        graph.InterfaceImpls = concretesByType
            .Select(kv => new InterfaceImplsInfo(
                TypeFqn: kv.Key,
                Concretes: kv.Value.OrderBy(s => s, StringComparer.Ordinal).ToList()))
            .OrderBy(info => info.TypeFqn, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// For an interface-method dispatch site: every concrete in-source class
    /// implementing the interface. For a virtual-class-method dispatch site:
    /// every concrete in-source class in the from-type's derivation tree
    /// (including the from-type itself, if concrete).
    /// </summary>
    private static async Task<List<INamedTypeSymbol>> FindRelevantConcreteClassesAsync(
        INamedTypeSymbol fromType, Solution solution)
    {
        var result = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        if (fromType.TypeKind == TypeKind.Interface)
        {
            // Direct implementers + transitive derived classes. Roslyn's
            // FindImplementationsAsync returns "the type that declares the
            // implementation" for each concrete class; we then walk derived
            // types to pick up classes that inherit the impl from a base.
            var direct = await SymbolFinder.FindImplementationsAsync(fromType, solution).ConfigureAwait(false);
            foreach (var sym in direct.OfType<INamedTypeSymbol>())
            {
                await CollectInSourceConcreteAsync(sym, solution, seen, result).ConfigureAwait(false);
            }
        }
        else
        {
            // Virtual class method case. Walk the type itself + all derived.
            await CollectInSourceConcreteAsync(fromType, solution, seen, result).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task CollectInSourceConcreteAsync(
        INamedTypeSymbol root, Solution solution, HashSet<INamedTypeSymbol> seen, List<INamedTypeSymbol> output)
    {
        if (!seen.Add(root.OriginalDefinition)) return;
        if (root.TypeKind == TypeKind.Class && !root.IsAbstract && root.DeclaringSyntaxReferences.Length > 0)
        {
            output.Add(root);
        }
        var derived = await SymbolFinder.FindDerivedClassesAsync(root, solution).ConfigureAwait(false);
        foreach (var d in derived)
        {
            await CollectInSourceConcreteAsync(d, solution, seen, output).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Find which method on <paramref name="concrete"/>'s vtable resolves the
    /// dispatch to <paramref name="dispatchMethod"/>.
    /// <list type="bullet">
    ///   <item>Interface methods: <c>concrete.FindImplementationForInterfaceMember</c>.
    ///     Cross-compilation pitfall: <c>FindImplementationForInterfaceMember</c>
    ///     requires the interface member symbol to come from the same
    ///     Compilation as <c>concrete</c>. We work around it by looking up
    ///     the matching method via <c>concrete.AllInterfaces</c> (which
    ///     contains symbols bound in concrete's own compilation), matching
    ///     by FQN to bridge the SymbolEqualityComparer mismatch.</item>
    ///   <item>Virtual class methods: walk up the inheritance chain of
    ///     <paramref name="concrete"/>, returning the most-derived override
    ///     of <paramref name="dispatchMethod"/> at or above <paramref name="concrete"/>.</item>
    /// </list>
    /// </summary>
    private static IMethodSymbol? ResolveImplFor(INamedTypeSymbol concrete, IMethodSymbol dispatchMethod)
    {
        var dispatchType = dispatchMethod.ContainingType;
        if (dispatchType == null) return null;

        if (dispatchType.TypeKind == TypeKind.Interface)
        {
            // Cross-compilation-safe interface lookup: find concrete's view
            // of the same interface (matched by FQN), then find the matching
            // method on it (matched by signature), then ask Roslyn to resolve
            // the implementing method.
            var dispatchTypeFqn = dispatchType.OriginalDefinition.ToDisplayString();
            var ifaceInConcrete = concrete.AllInterfaces.FirstOrDefault(
                i => i.OriginalDefinition.ToDisplayString() == dispatchTypeFqn);
            if (ifaceInConcrete == null) return null;

            IMethodSymbol? matchedMember = null;
            foreach (var m in ifaceInConcrete.GetMembers(dispatchMethod.Name).OfType<IMethodSymbol>())
            {
                if (SignaturesMatch(m, dispatchMethod))
                {
                    matchedMember = m;
                    break;
                }
            }
            if (matchedMember == null) return null;

            return concrete.FindImplementationForInterfaceMember(matchedMember) as IMethodSymbol;
        }

        // Virtual class method: walk concrete's base chain, returning the
        // first method whose containing type is in the chain and whose
        // signature matches dispatchMethod.
        for (var t = concrete; t != null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers(dispatchMethod.Name).OfType<IMethodSymbol>())
            {
                if (!SignaturesMatch(m, dispatchMethod)) continue;
                // Same logical method? We've reached either an override on
                // a derived class, or the original on dispatchType itself.
                return m;
            }
        }
        return null;
    }

    /// <summary>
    /// Best-effort signature match between two methods, used to bridge
    /// IMethodSymbol identities across project compilations (where
    /// <c>SymbolEqualityComparer.Default</c> says "not equal" even for the
    /// same logical method). Compares parameter count, generic arity, and
    /// each parameter type's display string.
    /// </summary>
    private static bool SignaturesMatch(IMethodSymbol a, IMethodSymbol b)
    {
        if (a.Name != b.Name) return false;
        if (a.Parameters.Length != b.Parameters.Length) return false;
        if (a.Arity != b.Arity) return false;
        for (var i = 0; i < a.Parameters.Length; i++)
        {
            if (a.Parameters[i].Type.ToDisplayString() != b.Parameters[i].Type.ToDisplayString()) return false;
        }
        return true;
    }
}
