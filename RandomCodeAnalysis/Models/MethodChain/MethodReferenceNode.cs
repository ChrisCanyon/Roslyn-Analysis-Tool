using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RandomCodeAnalysis.Models.MethodChain
{
    public class MethodReferenceNodeComparer : IEqualityComparer<MethodReferenceNode>
    {
        public bool Equals(MethodReferenceNode? x, MethodReferenceNode? y)
        {
            if (x is null || y is null)
                return false;

            return SymbolEqualityComparer.Default.Equals(
            x.ReferencedMethod,
            y.ReferencedMethod);
        }

        public int GetHashCode(MethodReferenceNode? obj)
        {
            return GetKey(obj).GetHashCode();
        }

        private static string GetKey(MethodReferenceNode? node)
        {
            return node == null ? "" : node.ReferencedMethod.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
    public sealed record ReferenceSite(DocumentId DocumentId, TextSpan Span);

    public class MethodReferenceNode
    {
        public string MethodName { get; private set; }
        public List<MethodReferenceNode> CallerNodes = new List<MethodReferenceNode>();

        [JsonIgnore]
        public IMethodSymbol ReferencedMethod { get; private set; }
        
        // Pointers to callsites
        [JsonIgnore]
        public List<ReferenceSite> CallSites { get; private set; }

        private MethodReferenceNode(IMethodSymbol referencedMethod)
        {
            ReferencedMethod = referencedMethod;
            MethodName = referencedMethod.ToDisplayString();
        }

        public static async Task<MethodReferenceNode> CreateAsync(
            IEnumerable<ReferencedSymbol> references,
            IMethodSymbol referencedMethod,
            Solution solution)
        {
            var node = new MethodReferenceNode(referencedMethod);
            node.CallSites = await BuildInvocationCallSitesAsync(references, solution).ConfigureAwait(false);
            return node;
        }

        private static async Task<List<ReferenceSite>> BuildInvocationCallSitesAsync(
            IEnumerable<ReferencedSymbol> references,
            Solution solution)
        {
            var results = new List<ReferenceSite>();

            foreach (var loc in references.SelectMany(r => r.Locations).Select(x => x.Location))
            {
                if (!loc.IsInSource || loc.SourceTree == null)
                    continue;

                var doc = solution.GetDocument(loc.SourceTree);
                if (doc == null)
                    continue;

                // Save the reference location, not an invocation span
                results.Add(new ReferenceSite(doc.Id, loc.SourceSpan));
            }

            return results
                    .DistinctBy(x => (x.DocumentId, x.Span))
                    .ToList();
        }
    }
}
