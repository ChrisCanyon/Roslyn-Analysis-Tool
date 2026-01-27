using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Newtonsoft.Json;
using RandomCodeAnalysis.Models.MethodChain;
using System.Security.Cryptography.Xml;
using System.Security.Policy;
using System.Xml.Linq;

namespace RandomCodeAnalysis.Analyzers
{
    public class CallChainAnalyzer
    {
        private readonly Solution _solution;
        private readonly MethodReferenceCache _referenceCache;

        public CallChainAnalyzer(Solution solution)
        {
            _solution = solution;
            _referenceCache = new MethodReferenceCache(solution);
        }

        public async Task<MethodReferenceNode> FindFullMethodChain(string fullyQualifiedTypeName, string methodName, bool shallow)
        {
            Console.WriteLine($"[FindFullMethodChain] Starting analysis for {fullyQualifiedTypeName}.{methodName}");

            var methodSymbols = await GetMethodsFromString(methodName, fullyQualifiedTypeName).ConfigureAwait(false);

            if (methodSymbols.Count > 1)
            {
                Console.WriteLine($"[FindFullMethodChain] Found {methodSymbols.Count} overloads");
            }

            var methodSymbol = methodSymbols.First();
            Console.WriteLine($"[FindFullMethodChain] Finding references...");
            var references = await _referenceCache.FindAllReferencesAsync(methodSymbols).ConfigureAwait(false);

            Console.WriteLine($"[FindFullMethodChain] Creating top node...");
            var topNode = await MethodReferenceNode.CreateAsync(references,methodSymbol, _solution).ConfigureAwait(false);
            Console.WriteLine($"[FindFullMethodChain] Top node has {topNode.CallSites.Count} call sites");

            var allNodes = new List<MethodReferenceNode>();
            await BuildReferenceChain(topNode, allNodes, shallow).ConfigureAwait(false);

            Console.WriteLine($"[FindFullMethodChain] Complete! Total nodes visited: {allNodes.Count}");
            Console.WriteLine($"[FindFullMethodChain] Reference cache size: {_referenceCache.CacheSize} methods");
            return topNode;
        }

        private async Task BuildReferenceChain(MethodReferenceNode node, List<MethodReferenceNode> visited, bool shallow = false)
        {
            var cmp = new MethodReferenceNodeComparer();
            if (visited.Contains(node, cmp))
            {
                Console.WriteLine($"[BuildReferenceChain] SKIPPED (already visited): {node.MethodName}");
                return;
            }
            visited.Add(node);

            // Only log if this method has a lot of call sites (potential problem)
            if (node.CallSites.Count > 50)
            {
                Console.WriteLine($"[WARN] {node.ReferencedMethod.Name} has {node.CallSites.Count} call sites (IsOverride: {node.ReferencedMethod.IsOverride}, IsVirtual: {node.ReferencedMethod.IsVirtual})");
            }

            // Track caller nodes we've already added to THIS node to prevent duplicates
            var addedCallers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

            // Phase 1: Identify all unique containing methods from call sites (fast)
            var containingMethods = new List<IMethodSymbol>();
            int skippedCount = 0;
            int alreadyVisitedCount = 0;
            foreach (var site in node.CallSites)
            {
                var document = _solution.GetDocument(site.DocumentId);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
                if (semanticModel == null) continue;

                var containingMethod = GetContainingMethod(semanticModel, site.Span.Start);
                if (containingMethod == null)
                {
                    skippedCount++;
                    continue;
                }

                // Skip if we've already added this caller to this node
                if (!addedCallers.Add(containingMethod))
                    continue;

                //skip if self reference
                if (SymbolEqualityComparer.Default.Equals(containingMethod, node.ReferencedMethod))
                {
                    skippedCount++;
                    continue;
                }

                // Skip if already visited (avoid expensive FindAllReferences call later)
                if (visited.Any(visitedNode =>
                    SymbolEqualityComparer.Default.Equals(visitedNode.ReferencedMethod, containingMethod)))
                {
                    alreadyVisitedCount++;
                    continue;
                }

                Console.WriteLine($"Adding containing method: {containingMethod.ToDisplayString()}");
                containingMethods.Add(containingMethod);
            }

            if (skippedCount > 0)
            {
                Console.WriteLine($"[INFO] Filtered out {skippedCount} call sites from {node.ReferencedMethod.Name}");
            }

            if (alreadyVisitedCount > 0)
            {
                Console.WriteLine($"[OPTIMIZE] Skipped {alreadyVisitedCount} already-visited methods (avoiding expensive FindAllReferences calls)");
            }

            // Phase 2: Find references for NEW methods only (slow, but parallelized with caching)
            var referenceTasks = containingMethods.Select(method =>
                Task.Run(() => _referenceCache.FindAllReferencesAsync(method))
            ).ToList();

            var allReferences = await Task.WhenAll(referenceTasks).ConfigureAwait(false);

            // Phase 3: Build nodes and recurse (fast)
            for (int i = 0; i < containingMethods.Count; i++)
            {
                var newRefNode = await MethodReferenceNode.CreateAsync(allReferences[i], containingMethods[i], _solution).ConfigureAwait(false);
                node.CallerNodes.Add(newRefNode);

                if (visited.Contains(newRefNode, cmp)) continue;
                if (!shallow) await BuildReferenceChain(newRefNode, visited).ConfigureAwait(false);
            }
        }

        private IMethodSymbol? GetContainingMethod(SemanticModel semanticModel, int position)
        {
            var symbol = semanticModel.GetEnclosingSymbol(position);

            // Walk up to find the first method symbol
            while (symbol != null && symbol is not IMethodSymbol)
            {
                symbol = symbol.ContainingSymbol;
            }

            var containingMethod = symbol as IMethodSymbol;

            // Skip over anonymous functions/lambdas to get the real containing method
            while (containingMethod != null &&
                   (containingMethod.MethodKind == MethodKind.AnonymousFunction ||
                    containingMethod.MethodKind == MethodKind.LocalFunction ||
                    containingMethod.MethodKind == MethodKind.LambdaMethod))
            {
                symbol = containingMethod.ContainingSymbol;

                // Walk up to find next method symbol (or give up if we hit non-method symbols)
                while (symbol != null && symbol is not IMethodSymbol)
                {
                    // If we've walked up to a type/namespace level, there's no containing method
                    if (symbol is INamedTypeSymbol || symbol is INamespaceSymbol)
                    {
                        return null;
                    }
                    symbol = symbol.ContainingSymbol;
                }

                // If we broke out because we hit a non-method, stop
                if (symbol is not IMethodSymbol)
                    break;

                containingMethod = symbol as IMethodSymbol;
            }

            // Skip methods from types not in source code (framework/compiled types we can't convert)
            if (containingMethod != null &&
                containingMethod.ContainingType?.DeclaringSyntaxReferences.Length == 0)
            {
                return null;
            }

            return containingMethod;
        }


        private async Task<List<IMethodSymbol>> GetConstructorsFromString(string fullyQualifiedTypeName)
        {
            var ret = new List<IMethodSymbol>();

            var tasks = new List<Task<Compilation?>>();
            foreach (var project in _solution.Projects)
            {
                tasks.Add(project.GetCompilationAsync());
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                var compilation = task.Result;
                if (compilation == null) continue;

                var typeSymbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
                if (typeSymbol == null) continue;

                ret.AddRange(typeSymbol
                    .GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m =>
                        m.MethodKind == MethodKind.Constructor));
            }

            if (ret.Count == 0)
            {
                Console.WriteLine($"[WARN] Could not resolve constructor for {fullyQualifiedTypeName}. No references in solution");
            }

            //dedupe
            ret = ret.GroupBy(m => $"{m.ContainingAssembly.Identity}_{m.ToDisplayString()}")
                    .Select(g => g.First())
                    .ToList();

            return ret;
        }

        private async Task<List<IMethodSymbol>> GetMethodsFromString(string methodName, string fullyQualifiedTypeName)
        {
            var ret = new List<IMethodSymbol>();

            var tasks = new List<Task<Compilation?>>();
            foreach (var project in _solution.Projects)
            {
                tasks.Add(project.GetCompilationAsync());
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                var compilation = task.Result;
                if (compilation == null) continue;

                var typeSymbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
                if (typeSymbol == null) continue;

                ret.AddRange(typeSymbol
                    .GetMembers(methodName)
                    .OfType<IMethodSymbol>());
            }

            if (ret.Count == 0)
            {
                Console.WriteLine($"[WARN] Could not resolve method {fullyQualifiedTypeName}.{methodName}. No references in solution");
            }

            //dedupe
            ret = ret.GroupBy(m => $"{m.ContainingAssembly.Identity}_{m.ToDisplayString()}")
                    .Select(g => g.First())
                    .ToList();

            return ret;
        }
    }
}
