using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Comparers
{
    public class RegInfoKeyComparer : IEqualityComparer<(INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime)>
    {
        public bool Equals(
            (INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime) x,
            (INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime) y)
        {
            var comparer = new FullyQualifiedNameComparer();
            return comparer.Equals(x.ImplementationType, y.ImplementationType) &&
                    comparer.Equals(x.ServiceInterface, y.ServiceInterface) &&
                    x.lifetime == y.lifetime &&
                    x.projectName == y.projectName;

        }

        public int GetHashCode((INamedTypeSymbol?, INamedTypeSymbol?, string, LifetimeTypes) obj)
        {
            unchecked
            {
                var cmp = new FullyQualifiedNameComparer();
                var hash = 17;
                hash = hash * 31 + cmp.GetHashCode(obj.Item1);
                hash = hash * 31 + cmp.GetHashCode(obj.Item2);
                hash = hash * 31 + obj.Item3.GetHashCode();
                hash = hash * 31 + obj.Item4.GetHashCode();
                return hash;
            }
        }
    }
}
