using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GatewayCallGraph;

/// <summary>
/// Scans a Solution and returns every controller action method. "Controller
/// action" follows the same predicate as ControllerEndpointDetector
/// (inherits Controller / ControllerBase / ApiController; public, ordinary,
/// instance method declared on a derived type). Used to populate the
/// controller-picker dropdown in the UI.
/// </summary>
public sealed class ControllerEnumerator
{
    private readonly Solution _solution;

    public ControllerEnumerator(Solution solution)
    {
        _solution = solution;
    }

    public sealed record ControllerActionInfo(
        string ActionFqn,             // unique key used by the API (Method's full display string)
        string ControllerType,        // fully-qualified containing type
        string MethodName,            // the action's bare method name
        string ProjectName,           // project the controller lives in (for grouping)
        string? File,                 // declaring file path (for tooltips)
        int Line);                    // declaring line number

    public async Task<List<ControllerActionInfo>> ListAsync()
    {
        var results = new List<ControllerActionInfo>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in _solution.Projects)
        {
            // Test projects are noisy and not interesting for this UI. Same exclusion
            // MethodReferenceCache uses, kept consistent here.
            if (project.Name.Contains("test", StringComparison.OrdinalIgnoreCase)) continue;

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync().ConfigureAwait(false);

                foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol typeSymbol) continue;
                    if (typeSymbol.IsAbstract) continue;

                    foreach (var methodDecl in classDecl.Members.OfType<MethodDeclarationSyntax>())
                    {
                        if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol methodSymbol) continue;
                        if (!ControllerEndpointDetector.IsControllerAction(methodSymbol)) continue;

                        var fqn = methodSymbol.ToDisplayString();
                        if (!seenKeys.Add(fqn)) continue;

                        var location = methodDecl.Identifier.GetLocation();
                        var lineSpan = location.GetLineSpan();

                        results.Add(new ControllerActionInfo(
                            ActionFqn: fqn,
                            ControllerType: methodSymbol.ContainingType.ToDisplayString(),
                            MethodName: methodSymbol.Name,
                            ProjectName: project.Name,
                            File: lineSpan.Path,
                            Line: lineSpan.StartLinePosition.Line + 1));
                    }
                }
            }
        }

        return results
            .OrderBy(r => r.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ControllerType, StringComparer.Ordinal)
            .ThenBy(r => r.MethodName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Resolves a fully-qualified action FQN (as produced by <see cref="ListAsync"/>)
    /// back to its IMethodSymbol. Returns null if no matching method is found.
    /// </summary>
    public async Task<IMethodSymbol?> ResolveAsync(string actionFqn)
    {
        foreach (var project in _solution.Projects)
        {
            if (project.Name.Contains("test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync().ConfigureAwait(false);

                foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol methodSymbol) continue;
                    if (methodSymbol.ToDisplayString() == actionFqn) return methodSymbol;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves any in-source method (not just controller actions) back to its
    /// IMethodSymbol. Used for click-to-drill where the user clicks an
    /// intermediate node in the graph and we re-root the graph at that node.
    /// </summary>
    public async Task<IMethodSymbol?> ResolveAnyMethodAsync(string methodFqn)
    {
        foreach (var project in _solution.Projects)
        {
            if (project.Name.Contains("test", StringComparison.OrdinalIgnoreCase)) continue;
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync().ConfigureAwait(false);

                foreach (var methodDecl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(methodDecl) is not IMethodSymbol methodSymbol) continue;
                    if (methodSymbol.ToDisplayString() == methodFqn) return methodSymbol;
                }
            }
        }
        return null;
    }
}
