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
    public async Task<CallGraph> BuildAsync(IMethodSymbol rootMethod)
    {
        var graph = new CallGraph();
        await AddIntoAsync(graph, rootMethod);
        return graph;
    }

    /// <summary>
    /// Adds the down-walk of <paramref name="rootMethod"/> into an existing
    /// graph. Useful when seeding multiple roots into one graph (e.g. all
    /// controllers in a single project).
    /// </summary>
    public async Task AddIntoAsync(CallGraph graph, IMethodSymbol rootMethod)
    {
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var rootId = graph.GetOrAddNode(rootMethod);

        if (ControllerEndpointDetector.IsControllerAction(rootMethod))
            graph.PromoteKind(rootId, NodeKind.ControllerAction);
        if (MatchBoundary(rootMethod) is { } rootBoundary)
            graph.TagBoundary(rootId, rootBoundary);

        await WalkAsync(graph, rootMethod, depth: 0, visited).ConfigureAwait(false);
    }

    private async Task WalkAsync(CallGraph graph, IMethodSymbol caller, int depth, HashSet<IMethodSymbol> visited)
    {
        if (depth >= _maxDepth) return;
        if (!visited.Add(caller)) return;

        // If the caller IS a boundary, stop — we don't recurse into a boundary's
        // body; the seed list defines our terminating boundary on the way down.
        if (MatchBoundary(caller) != null) return;

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

                var effectiveBoundary = calleeBoundary ?? inferredSurface ?? primitiveLeaf;

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
                var callSite = BuildCallSite(invocation);
                graph.AddEdge(callerId, calleeId, callSite, loop);

                // Boundary: don't recurse, don't fan out. The boundary is the leaf
                // even if it's an interface (we want the chain to stop here).
                if (effectiveBoundary != null) continue;

                // Interface / virtual dispatch: fan out into implementations. Skip
                // the fan-out entirely if the implementation set is too large
                // (typically a generic base interface like IRepository<T> with
                // hundreds of implementations) — these explode the graph and
                // rarely add useful information for a single-controller view.
                if (callee.IsAbstract || callee.IsVirtual || callee.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    var impls = (await SymbolFinder.FindImplementationsAsync(callee, _solution).ConfigureAwait(false))
                        .OfType<IMethodSymbol>()
                        .Where(m => HasSourceInSolution(m.OriginalDefinition))
                        .ToList();

                    if (impls.Count <= _maxFanout)
                    {
                        foreach (var implSym in impls)
                        {
                            var impl = implSym.OriginalDefinition;

                            var implId = graph.GetOrAddNode(impl);
                            // Same three-layer classification as the direct-call path —
                            // an impl reached via dispatch fan-out should also be tagged
                            // when it's an inferred surface (e.g. *Provider, *Helper).
                            var implBoundary = MatchBoundary(impl);
                            if (implBoundary == null)
                            {
                                var implDecision = await _inferrer.ClassifyAsync(impl).ConfigureAwait(false);
                                if (implDecision.Category != null && implDecision.Surface)
                                    implBoundary = BoundarySeeds.GetByName(implDecision.Category);
                            }
                            if (implBoundary != null) graph.TagBoundary(implId, implBoundary);
                            graph.AddDispatchEdge(calleeId, implId);

                            if (implBoundary != null) continue; // boundary impl is a leaf
                            if (visited.Contains(impl)) continue;
                            await WalkAsync(graph, impl, depth + 1, visited).ConfigureAwait(false);
                        }
                    }
                    // else: too many impls — treat the interface as a leaf. The
                    // edge to the interface is still in the graph from above.
                }

                // Recurse into the callee's own body if it has one in source.
                if (!HasSourceInSolution(callee)) continue; // BCL / third-party
                if (visited.Contains(callee)) continue;     // already walked

                await WalkAsync(graph, callee, depth + 1, visited).ConfigureAwait(false);
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
