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
        public string SimpleMethodName { get; private set; }
        public List<MethodReferenceNode> CallerNodes = new List<MethodReferenceNode>();

        [JsonIgnore]
        public IMethodSymbol ReferencedMethod { get; set; }
        
        // Pointers to callsites
        [JsonIgnore]
        public List<ReferenceSite> CallSites { get; private set; }

        private MethodReferenceNode(IMethodSymbol referencedMethod)
        {
            ReferencedMethod = referencedMethod;
            MethodName = referencedMethod.ToDisplayString();
            SimpleMethodName = referencedMethod.Name;
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

            foreach (var refLocation in references.SelectMany(r => r.Locations))
            {
                var loc = refLocation.Location;
                if (!loc.IsInSource || loc.SourceTree == null)
                    continue;

                var doc = solution.GetDocument(loc.SourceTree);
                if (doc == null)
                    continue;

                // Filter out non-invocation references (nameof, string literals, etc.)
                // We need to check the syntax node to see if it's actually a method invocation
                var root = await doc.GetSyntaxRootAsync().ConfigureAwait(false);
                if (root == null)
                    continue;

                var node = root.FindNode(loc.SourceSpan);

                // Check if this is an actual invocation or member access that leads to invocation
                // Skip if it's inside a nameof() expression or attribute argument
                if (!IsActualInvocation(node))
                    continue;

                // Save the reference location, not an invocation span
                results.Add(new ReferenceSite(doc.Id, loc.SourceSpan));
            }

            return results
                    .DistinctBy(x => (x.DocumentId, x.Span))
                    .ToList();
        }

        private static bool IsActualInvocation(SyntaxNode node)
        {
            // Walk up the tree to understand the context
            var current = node;
            while (current != null)
            {
                // If we find a nameof expression, this is NOT an invocation
                if (current is InvocationExpressionSyntax invocation)
                {
                    // Check if this is a nameof() expression
                    if (invocation.Expression is IdentifierNameSyntax identifier &&
                        identifier.Identifier.Text == "nameof")
                    {
                        return false;
                    }
                }

                // If we're inside an attribute argument, skip it
                if (current is AttributeSyntax)
                {
                    return false;
                }

                // If we hit an invocation expression where the reference is the invoked method, it's valid
                if (current is InvocationExpressionSyntax inv)
                {
                    // The reference is part of the method being invoked
                    return true;
                }

                current = current.Parent;
            }

            // If we never found an invocation context, it's not an actual call
            return false;
        }
    }
}
