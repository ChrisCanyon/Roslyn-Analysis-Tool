using Microsoft.CodeAnalysis;
using RandomCodeAnalysis.Models.MethodChain;

namespace GatewayCallGraph;

/// <summary>
/// Walks the caller tree produced by CallChainAnalyzer (root = gateway method;
/// children of a node = methods that call that node) and accumulates everything
/// into a single <see cref="CallGraph"/>: one node per distinct method symbol,
/// one edge per distinct (caller, callee, callSite) triple.
///
/// Each edge carries optional <see cref="LoopInfo"/> describing whether that
/// specific call site is enclosed in a loop (statement loop or LINQ-enumerable
/// lambda). This is what makes N-squared-style hot paths visible downstream.
/// </summary>
public sealed class GraphBuilder
{
    private readonly Solution _solution;
    private readonly CallGraph _graph;

    public GraphBuilder(Solution solution, CallGraph graph)
    {
        _solution = solution;
        _graph = graph;
    }

    /// <summary>
    /// Adds a boundary method's caller tree to the graph. The boundary node itself
    /// is tagged with its category. Any caller in the tree that satisfies the
    /// controller-action predicate is promoted to ControllerAction.
    /// </summary>
    public async Task AddTreeAsync(MethodReferenceNode gatewayRoot)
    {
        var gatewayId = _graph.GetOrAddNode(gatewayRoot.ReferencedMethod);
        if (MatchBoundary(gatewayRoot.ReferencedMethod) is { } boundary)
            _graph.TagBoundary(gatewayId, boundary);

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        await WalkAsync(gatewayRoot, visited).ConfigureAwait(false);
    }

    private static BoundaryCategory? MatchBoundary(IMethodSymbol method)
    {
        var typeFullName = method.ContainingType?.ToDisplayString();
        if (typeFullName == null) return null;
        return BoundarySeeds.Match(typeFullName, method.Name);
    }

    private async Task WalkAsync(MethodReferenceNode calleeNode, HashSet<IMethodSymbol> visited)
    {
        if (!visited.Add(calleeNode.ReferencedMethod)) return;

        var calleeId = _graph.GetOrAddNode(calleeNode.ReferencedMethod);

        // Each entry in calleeNode.CallSites is a place where calleeNode.ReferencedMethod
        // is invoked. The containing-method at that location is the caller. We need
        // both the caller symbol (to identify the graph node) and the syntax position
        // (to detect loops).
        foreach (var site in calleeNode.CallSites)
        {
            var document = _solution.GetDocument(site.DocumentId);
            if (document == null) continue;

            var syntaxRoot = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
            if (syntaxRoot == null || semanticModel == null) continue;

            var siteNode = syntaxRoot.FindNode(site.Span);
            if (siteNode == null) continue;

            var callerSymbol = ContainingMethod(semanticModel, site.Span.Start);
            if (callerSymbol == null) continue;

            // Self-reference (recursion) — the analyzer already terminates these branches;
            // skip so we don't add a self-loop edge that confuses the graph.
            if (SymbolEqualityComparer.Default.Equals(callerSymbol, calleeNode.ReferencedMethod))
                continue;

            var callerId = _graph.GetOrAddNode(callerSymbol);
            if (ControllerEndpointDetector.IsControllerAction(callerSymbol))
                _graph.PromoteKind(callerId, NodeKind.ControllerAction);

            var loop = LoopDetector.FindEnclosingLoop(siteNode, semanticModel);
            var callSite = BuildCallSite(siteNode);
            _graph.AddEdge(callerId, calleeId, callSite, loop);
        }

        foreach (var caller in calleeNode.CallerNodes)
        {
            await WalkAsync(caller, visited).ConfigureAwait(false);
        }
    }

    private static IMethodSymbol? ContainingMethod(SemanticModel semanticModel, int position)
    {
        var symbol = semanticModel.GetEnclosingSymbol(position);

        while (symbol != null && symbol is not IMethodSymbol)
            symbol = symbol.ContainingSymbol;

        var method = symbol as IMethodSymbol;

        // Skip past lambdas / local functions / anonymous functions to find the real
        // enclosing method. This is how we associate "the call inside a lambda inside
        // PaymentManager.Charge" with PaymentManager.Charge as the caller node, while
        // LoopDetector independently flags the lambda's outer LINQ invocation.
        while (method != null && (
            method.MethodKind == MethodKind.AnonymousFunction ||
            method.MethodKind == MethodKind.LocalFunction ||
            method.MethodKind == MethodKind.LambdaMethod))
        {
            symbol = method.ContainingSymbol;
            while (symbol != null && symbol is not IMethodSymbol)
            {
                if (symbol is INamedTypeSymbol || symbol is INamespaceSymbol) return null;
                symbol = symbol.ContainingSymbol;
            }
            if (symbol is not IMethodSymbol) break;
            method = symbol as IMethodSymbol;
        }

        // Skip framework/compiled methods that aren't in source we can analyze.
        if (method != null && method.ContainingType?.DeclaringSyntaxReferences.Length == 0)
            return null;

        return method;
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
