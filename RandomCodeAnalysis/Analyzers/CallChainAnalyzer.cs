using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Newtonsoft.Json;
using RandomCodeAnalysis.Models.MethodChain;
using System.Security.Cryptography.Xml;
using System.Xml.Linq;

namespace RandomCodeAnalysis.Analyzers
{
    public static class CallChainAnalyzer
    {
        public static async Task FindFullMethodChain(Solution solution)
        {
            var fullyQualifiedTypeName = "Infrastructure.Incode.InvisionGateway.Common.RestApi.RestApiBase";
            var methodName = "ExecuteWebRequest";

            //            var methodSymbols = await GetConstructorsFromString(fullyQualifiedTypeName, solution);
            var methodSymbols = await GetMethodsFromString(methodName, fullyQualifiedTypeName, solution);
            
            if (methodSymbols.Count > 1)
            {
                //confused
            }

            var methodSymbol = methodSymbols.First();
            var references = await FindAllReferences(methodSymbols, solution);

            var topNode = new MethodReferenceNode(references);
            var allNodes = new List<MethodReferenceNode>();
            await BuildReferenceChain(topNode, solution, allNodes);
            _ = topNode;
            var asString = JsonConvert.SerializeObject(topNode);
            _ = asString;
        }

        private static async Task BuildReferenceChain(MethodReferenceNode node, Solution solution, List<MethodReferenceNode> visited)
        {
            var cmp = new MethodReferenceNodeComparer();
            if (visited.Contains(node, cmp)) return;
            visited.Add(node);

            foreach (var location in node.ReferenceLocations)
            {
                var sourceTree = location.SourceTree;
                if (sourceTree == null) continue;

                var span = location.SourceSpan;

                // Get the project/compilation that owns this tree
                var document = solution.GetDocument(sourceTree);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync();
                if (semanticModel == null) continue;

                var symbol = semanticModel.GetEnclosingSymbol(span.Start);
                IMethodSymbol? method = null;

                for (var s = symbol; s is IMethodSymbol m; s = s.ContainingSymbol)
                {
                    if (m.MethodKind == MethodKind.Ordinary ||
                        m.MethodKind == MethodKind.Constructor ||
                        m.MethodKind == MethodKind.StaticConstructor)
                    {
                        method = m;
                        break;
                    }
                }

                if (method == null)
                    continue; // still something weird (field initializer, etc.)

                // Now you have the containing method symbol
                var containingMethod = method;

                //find refrences
                var references = await FindAllReferences(containingMethod, solution);

                //if no references return
                if (references.Count() == 0) continue;
                var newRefNode = new MethodReferenceNode(references);
                node.References.Add(newRefNode);

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
