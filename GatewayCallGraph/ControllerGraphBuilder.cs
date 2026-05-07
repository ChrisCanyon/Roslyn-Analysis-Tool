using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace GatewayCallGraph;

/// <summary>
/// Builds a call graph by walking *down* from a root method through every
/// invocation it contains, recursing into the bodies of in-source callees,
/// and terminating at:
///   - gateway methods (matched against a configured stop set)
///   - methods whose source is not in the loaded solution (BCL / third-party)
///   - already-visited symbols (cycle or shared intermediate)
///   - a configurable max depth (default 12)
///
/// Each edge is annotated by LoopDetector so call sites inside for/foreach/
/// while/do blocks or LINQ-enumerable lambdas are visible in the rendered graph.
///
/// Built to be the inverse of GraphBuilder (which walks UP from a gateway). Both
/// produce the same CallGraph data shape so the same renderer / UI can show either.
/// </summary>
public sealed class ControllerGraphBuilder
{
    private readonly Solution _solution;
    private readonly int _maxDepth;
    private readonly int _maxFanout;
    private readonly BoundaryInferrer _inferrer;

    public ControllerGraphBuilder(
        Solution solution,
        int maxDepth = 12,
        int maxFanout = 25)
    {
        _solution = solution;
        _maxDepth = maxDepth;
        _maxFanout = maxFanout;
        _inferrer = new BoundaryInferrer(solution);
    }

    /// <summary>
    /// Builds the call graph rooted at <paramref name="rootMethod"/>. The root
    /// node's NodeKind is left at the default Intermediate; callers should
    /// PromoteKind it to ControllerAction (or whatever fits their entry point).
    /// </summary>
    /// <param name="expandPastBoundaries">
    /// When true, the walker keeps descending into a node's body even after
    /// tagging it as a boundary.
    /// </param>
    /// <param name="hiddenImplFqns">
    /// Optional set of impl FQNs to skip during dispatch fan-out. Used by the
    /// UI to hide individual implementations of an interface. Matched against
    /// <c>impl.OriginalDefinition.ToDisplayString()</c>.
    /// </param>
    public async Task<CallGraph> BuildAsync(IMethodSymbol rootMethod, bool expandPastBoundaries = false, IReadOnlySet<string>? hiddenImplFqns = null)
    {
        var graph = new CallGraph();
        await AddIntoAsync(graph, rootMethod, expandPastBoundaries, hiddenImplFqns);
        return graph;
    }

    /// <summary>
    /// Adds the down-walk of <paramref name="rootMethod"/> into an existing
    /// graph. Useful when seeding multiple roots into one graph (e.g. all
    /// controllers in a single project).
    /// </summary>
    public async Task AddIntoAsync(CallGraph graph, IMethodSymbol rootMethod, bool expandPastBoundaries = false, IReadOnlySet<string>? hiddenImplFqns = null)
    {
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var rootId = graph.GetOrAddNode(rootMethod);

        if (ControllerEndpointDetector.IsControllerAction(rootMethod))
            graph.PromoteKind(rootId, NodeKind.ControllerAction);
        if (MatchBoundary(rootMethod) is { } rootBoundary)
            graph.TagBoundary(rootId, rootBoundary);

        await WalkAsync(graph, rootMethod, depth: 0, visited, expandPastBoundaries, hiddenImplFqns).ConfigureAwait(false);
    }

    private async Task WalkAsync(CallGraph graph, IMethodSymbol caller, int depth, HashSet<IMethodSymbol> visited, bool expandPastBoundaries, IReadOnlySet<string>? hiddenImplFqns)
    {
        if (depth >= _maxDepth) return;
        if (!visited.Add(caller)) return;

        // If the caller IS a boundary, normally we stop — the seed/inferrer
        // defines the terminating boundary on the way down. With
        // expandPastBoundaries, we walk into the boundary's body too.
        if (!expandPastBoundaries && MatchBoundary(caller) != null) return;

        foreach (var syntaxRef in caller.DeclaringSyntaxReferences)
        {
            var syntaxNode = await syntaxRef.GetSyntaxAsync().ConfigureAwait(false);
            if (syntaxNode is not BaseMethodDeclarationSyntax methodDecl) continue;

            var bodyNode = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
            if (bodyNode == null) continue;

            var document = _solution.GetDocument(syntaxNode.SyntaxTree);
            if (document == null) continue;

            var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
            if (semanticModel == null) continue;

            // Find every invocation in the caller's body. We do NOT descend into
            // lambdas explicitly here — they're regular descendants of the body
            // and their invocations are still seen, and LoopDetector handles the
            // "inside a LINQ lambda" case for us.
            var invocations = bodyNode.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                var calleeSymbol = symbolInfo.Symbol as IMethodSymbol
                                   ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                if (calleeSymbol == null) continue;

                // Skip self-references — the path-cycle case. They show up as
                // recursion in the rendered graph regardless via the visited set.
                if (SymbolEqualityComparer.Default.Equals(calleeSymbol, caller)) continue;

                var callee = calleeSymbol.OriginalDefinition;

                // Three layers of boundary classification, in priority order:
                //   1. BoundarySeeds — hand-curated. Wins outright.
                //   2. BoundaryInferrer — taint-from-leaf with intent-surface promotion.
                //      If the callee is the surface, tag and stop. If it's tainted
                //      but not a surface, walk through it; deeper frames get a chance.
                //   3. IoPrimitives leaf — if a tainted method has no surface above
                //      a framework primitive, the primitive itself is admitted as a
                //      leaf node (mechanism shows up rather than the boundary being
                //      lost). This is the "no good intent layer" fallback.
                var calleeBoundary = MatchBoundary(callee);
                BoundaryCategory? inferredSurface = null;

                if (calleeBoundary == null)
                {
                    var decision = await _inferrer.ClassifyAsync(callee).ConfigureAwait(false);
                    if (decision.Category != null && decision.Surface)
                    {
                        inferredSurface = BoundarySeeds.GetByName(decision.Category);
                    }
                }

                // Framework primitive leaf: if it's an I/O primitive, treat it as a
                // boundary in its own right so it shows up as a leaf when no surface
                // wraps it. Otherwise, framework methods are noise and we drop them.
                BoundaryCategory? primitiveLeaf = null;
                if (calleeBoundary == null && inferredSurface == null && !HasSourceInSolution(callee))
                {
                    if (TryMatchPrimitive(callee) is { } primCat)
                    {
                        primitiveLeaf = BoundarySeeds.GetByName(primCat);
                    }
                    else
                    {
                        // Not seeded, not inferred-surface, not a primitive, not in source.
                        // Standard BCL/third-party noise — drop.
                        continue;
                    }
                }

                // Resolve in-source impls up front so the routing decision below
                // ("is this an interface that should pass through to its impls?")
                // can use the same data the fan-out logic uses. Empty list for
                // non-interface/non-virtual callees.
                var inSourceImpls = await ResolveInSourceImplsAsync(callee).ConfigureAwait(false);
                // Apply user-side impl visibility filter. Hidden impls do NOT
                // appear as nodes, do NOT receive dispatch edges, and are NOT
                // walked into. The interface still routes to whatever's left.
                if (hiddenImplFqns != null && hiddenImplFqns.Count > 0 && inSourceImpls.Count > 0)
                {
                    inSourceImpls = inSourceImpls
                        .Where(m => !hiddenImplFqns.Contains(m.OriginalDefinition.ToDisplayString()))
                        .ToList();
                }
                var hasInSourceImpls = inSourceImpls.Count > 0 && inSourceImpls.Count <= _maxFanout;

                // Routing-vs-leaf rule: if the callee is an interface (or
                // abstract/virtual) AND it has in-source impls we'll be fanning
                // out into, treat it as a *routing* node — DON'T tag it as a
                // boundary even if a seed/inferrer match says we should. The
                // real boundary lives on the impl(s), where the actual I/O
                // pattern (DB vs HTTP vs mixed) is visible. This means a single
                // interface dispatched to two impls — one DB-touching, one
                // HTTP-touching — produces TWO boundary nodes with different
                // colors, instead of one ambiguous interface box.
                //
                // If there are no in-source impls (external SDK interface), the
                // interface itself is the surface — fall back to the original
                // behavior and tag it.
                BoundaryCategory? effectiveBoundary;
                if (hasInSourceImpls)
                {
                    effectiveBoundary = null; // interface is a routing node
                }
                else
                {
                    effectiveBoundary = calleeBoundary ?? inferredSurface ?? primitiveLeaf;
                }

                // Add the edge. Even if we won't recurse (boundary), the edge to the
                // callee node should appear so the boundary shows up as a leaf in the
                // rendered graph.
                var callerId = graph.GetOrAddNode(caller);
                var calleeId = graph.GetOrAddNode(callee);

                if (effectiveBoundary != null)
                    graph.TagBoundary(calleeId, effectiveBoundary);
                if (ControllerEndpointDetector.IsControllerAction(callee))
                    graph.PromoteKind(calleeId, NodeKind.ControllerAction);

                var loop = LoopDetector.FindEnclosingLoop(invocation, semanticModel);
                var conditional = ConditionalDetector.FindEnclosingConditional(invocation);
                var callSite = BuildCallSite(invocation);
                graph.AddEdge(callerId, calleeId, callSite, loop, conditional);

                // Boundary leaf (no in-source impls case): by default the walk
                // stops here so the boundary is the visible leaf. With
                // expandPastBoundaries we keep going.
                if (effectiveBoundary != null && !expandPastBoundaries) continue;

                // Interface / virtual dispatch: fan out into in-source impls.
                // Each impl is classified independently — different impls can
                // have different boundary categories.
                if (hasInSourceImpls)
                {
                    // The interface "would have been" tagged with this category
                    // before Feature 2; if it would have, propagate that tag to
                    // every impl. This keeps `IRepository.X` -> `XRepositoryEF`
                    // classified as database_query even though the EF class name
                    // doesn't match the *Repository regex on its own.
                    var interfaceFallbackBoundary = calleeBoundary ?? inferredSurface;

                    foreach (var implSym in inSourceImpls)
                    {
                        var impl = implSym.OriginalDefinition;

                        var implId = graph.GetOrAddNode(impl);
                        // Classify per impl: seed match on the impl, then
                        // inferrer, then fall back to whatever the interface
                        // itself matched. The fallback is what catches
                        // *RepositoryEF impls of *Repository interfaces.
                        var implBoundary = MatchBoundary(impl);
                        if (implBoundary == null)
                        {
                            var implDecision = await _inferrer.ClassifyAsync(impl).ConfigureAwait(false);
                            if (implDecision.Category != null && implDecision.Surface)
                                implBoundary = BoundarySeeds.GetByName(implDecision.Category);
                        }
                        implBoundary ??= interfaceFallbackBoundary;
                        if (implBoundary != null) graph.TagBoundary(implId, implBoundary);
                        graph.AddDispatchEdge(calleeId, implId);

                        // Boundary impl is normally a leaf; expandPastBoundaries
                        // walks into it the same as a non-boundary impl.
                        if (implBoundary != null && !expandPastBoundaries) continue;
                        if (visited.Contains(impl)) continue;
                        await WalkAsync(graph, impl, depth + 1, visited, expandPastBoundaries, hiddenImplFqns).ConfigureAwait(false);
                    }
                }

                // Recurse into the callee's own body if it has one in source.
                if (!HasSourceInSolution(callee)) continue; // BCL / third-party
                if (visited.Contains(callee)) continue;     // already walked

                await WalkAsync(graph, callee, depth + 1, visited, expandPastBoundaries, hiddenImplFqns).ConfigureAwait(false);
            }
        }
    }

    private static BoundaryCategory? MatchBoundary(IMethodSymbol method)
    {
        var typeFullName = method.ContainingType?.ToDisplayString();
        if (typeFullName == null) return null;
        return BoundarySeeds.Match(typeFullName, method.Name);
    }

    /// <summary>
    /// Returns the in-source impls of an interface / abstract / virtual method,
    /// or an empty list otherwise. Used both for the routing-vs-leaf decision
    /// and for the dispatch fan-out itself, so we resolve once per call site.
    /// </summary>
    private async Task<List<IMethodSymbol>> ResolveInSourceImplsAsync(IMethodSymbol callee)
    {
        var ct = callee.ContainingType;
        var isVirtualOrInterface =
            callee.IsAbstract || callee.IsVirtual || ct?.TypeKind == TypeKind.Interface;
        if (!isVirtualOrInterface) return new List<IMethodSymbol>();

        // Two Roslyn APIs cover different cases — we use both:
        //   - FindImplementationsAsync: for an interface method, returns each
        //     concrete class's direct implementer.
        //   - FindOverridesAsync: for a virtual method, returns subclass
        //     overrides. (Doesn't cover interface impls on its own.)
        // Then we transitively walk overrides since a subclass override can
        // itself be overridden one level deeper.
        var all = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var worklist = new Queue<IMethodSymbol>();

        foreach (var impl in (await SymbolFinder.FindImplementationsAsync(callee, _solution).ConfigureAwait(false)).OfType<IMethodSymbol>())
        {
            if (all.Add(impl)) worklist.Enqueue(impl);
        }
        // Also seed from the callee's own overrides — needed for virtuals where
        // FindImplementationsAsync returns nothing.
        foreach (var ov in (await SymbolFinder.FindOverridesAsync(callee, _solution).ConfigureAwait(false)).OfType<IMethodSymbol>())
        {
            if (all.Add(ov)) worklist.Enqueue(ov);
        }

        while (worklist.Count > 0)
        {
            var m = worklist.Dequeue();
            foreach (var ov in (await SymbolFinder.FindOverridesAsync(m, _solution).ConfigureAwait(false)).OfType<IMethodSymbol>())
            {
                if (all.Add(ov)) worklist.Enqueue(ov);
            }
        }

        return all
            .Where(m => HasSourceInSolution(m.OriginalDefinition))
            .ToList();
    }

    /// <summary>
    /// Match against the framework I/O primitive list, walking the type's base
    /// chain and implemented interfaces so SqlConnection.Open also matches as
    /// DbConnection.Open / IDbConnection.Open.
    /// </summary>
    private static string? TryMatchPrimitive(IMethodSymbol method)
    {
        for (var t = method.ContainingType; t != null; t = t.BaseType)
        {
            var fqn = t.ToDisplayString();
            if (IoPrimitives.Match(fqn, method.Name) is { } cat) return cat;
        }
        if (method.ContainingType is { } ct)
        {
            foreach (var iface in ct.AllInterfaces)
            {
                var fqn = iface.ToDisplayString();
                if (IoPrimitives.Match(fqn, method.Name) is { } cat) return cat;
            }
        }
        // Out-of-source HTTP-client-shaped call: generated Refit/Swagger clients
        // (CartClient, TransactionClient, etc.) wrap HttpClient inside a compiled
        // assembly. Treat them as external_system_gateway leaves.
        if (method.ContainingType is { } cls
            && cls.DeclaringSyntaxReferences.Length == 0)
        {
            var name = cls.Name;
            if ((name.EndsWith("Client", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("Api", StringComparison.OrdinalIgnoreCase))
                && !name.StartsWith("Http", StringComparison.Ordinal))
            {
                return IoPrimitives.ExternalSystemGateway;
            }
        }
        return null;
    }

    private static bool HasSourceInSolution(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Length == 0) return false;
        if (method.ContainingType?.DeclaringSyntaxReferences.Length == 0) return false;
        return true;
    }

    private static CallSite BuildCallSite(SyntaxNode siteNode)
    {
        var location = siteNode.GetLocation();
        var span = location.GetLineSpan();
        return new CallSite
        {
            File = span.Path,
            Line = span.StartLinePosition.Line + 1,
        };
    }
}
