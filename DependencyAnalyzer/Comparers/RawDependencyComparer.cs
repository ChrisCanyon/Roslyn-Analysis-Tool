using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Comparers
{
    public class RawDependencyComparer : IEqualityComparer<RawDependency>
    {
        public bool Equals(RawDependency? x, RawDependency? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            var cmp = new FullyQualifiedNameComparer();

            // Dependency identity is based only on the Type symbol
            return cmp.Equals(x.Type, y.Type);
        }

        public int GetHashCode(RawDependency obj)
        {
            return SymbolEqualityComparer.Default.GetHashCode(obj.Type);
        }
    }
}
