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
    public static class CallChainAnalyzer
    {
        public static async Task<MethodReferenceNode> FindFullMethodChain(Solution solution)
        {
            //var fullyQualifiedTypeName = "Infrastructure.Incode.InvisionGateway.Common.RestApi.RestApiBase";
            //var methodName = "ExecuteWebRequest";
            var fullyQualifiedTypeName = "RAT_Test.CacheManager";
            var methodName = "TESTMETHOD";

            //            var methodSymbols = await GetConstructorsFromString(fullyQualifiedTypeName, solution);
            var methodSymbols = await GetMethodsFromString(methodName, fullyQualifiedTypeName, solution);
            
            if (methodSymbols.Count > 1)
            {
                //confused
            }

            var methodSymbol = methodSymbols.First();
            var references = await FindAllReferences(methodSymbols, solution);

            var topNode = await MethodReferenceNode.CreateAsync(references,methodSymbol, solution);
            var allNodes = new List<MethodReferenceNode>();
            await BuildReferenceChain(topNode, solution, allNodes);

            return topNode;
        }

        private static async Task BuildReferenceChain(MethodReferenceNode node, Solution solution, List<MethodReferenceNode> visited)
        {
            var cmp = new MethodReferenceNodeComparer();
            if (visited.Contains(node, cmp)) return;
            visited.Add(node);

            foreach (var site in node.CallSites)
            {
                var document = solution.GetDocument(site.DocumentId);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
                if (semanticModel == null) continue;

                var symbol = semanticModel.GetEnclosingSymbol(site.Span.Start);
                IMethodSymbol? method = null;

                while (symbol != null && symbol is not IMethodSymbol)
                {
                    symbol = symbol.ContainingSymbol;
                }

                // Now you have the containing method symbol
                var containingMethod = symbol as IMethodSymbol;
                if (containingMethod == null)
                    continue;

                //find refrences
                var references = await FindAllReferences(containingMethod, solution);

                //build new node
                var newRefNode = await MethodReferenceNode.CreateAsync(references, containingMethod, solution);
                node.CallerNodes.Add(newRefNode);

                if (visited.Contains(newRefNode, cmp)) continue;
                await BuildReferenceChain(newRefNode, solution, visited);
            }
        }

        private static async Task<IEnumerable<ReferencedSymbol>> FindAllReferences(IMethodSymbol method, Solution solution)
        {
            var ret = new List<ReferencedSymbol>();
            ret.AddRange(await SymbolFinder.FindReferencesAsync(method, solution));
            return ret;
        }

        private static async Task<IEnumerable<ReferencedSymbol>> FindAllReferences(IEnumerable<IMethodSymbol> methods, Solution solution)
        {
            var ret = new List<ReferencedSymbol>();
            foreach (var method in methods)
            {
                ret.AddRange(await SymbolFinder.FindReferencesAsync(method, solution));
            }
            return ret;
        }

        private static async Task<List<IMethodSymbol>> GetConstructorsFromString(string fullyQualifiedTypeName, Solution solution)
        {
            var ret = new List<IMethodSymbol>();

            var tasks = new List<Task<Compilation?>>();
            foreach (var project in solution.Projects)
            {
                tasks.Add(project.GetCompilationAsync());
            }

            await Task.WhenAll(tasks);
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

        private static async Task<List<IMethodSymbol>> GetMethodsFromString(string methodName, string fullyQualifiedTypeName, Solution solution)
        {
            var ret = new List<IMethodSymbol>();

            var tasks = new List<Task<Compilation?>>();
            foreach (var project in solution.Projects)
            {
                tasks.Add(project.GetCompilationAsync());
            }

            await Task.WhenAll(tasks);
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
