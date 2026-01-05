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

    public class MethodReferenceNode
    {
        public string MethodName { get; private set; }
        public List<MethodReferenceNode> References = new List<MethodReferenceNode>();

        [JsonIgnore]
        public ISymbol ReferencedMethod { get; private set; }
        [JsonIgnore]
        public List<Location> ReferenceLocations { get; private set; }

        public MethodReferenceNode(IEnumerable<ReferencedSymbol> referenced)
        {
            ReferencedMethod = referenced.First().Definition;

            ReferenceLocations = referenced
                .SelectMany(l => l.Locations)
                .Select(l => l.Location)
                .Where(l => l.IsInSource)
                .DistinctBy(l => (l.SourceTree?.FilePath, l.SourceSpan.Start)).ToList();

            MethodName = ReferencedMethod.ToDisplayString();
        }
    }
}
