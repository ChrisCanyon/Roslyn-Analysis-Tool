using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class FullyQualifiedNameComparer : IEqualityComparer<INamedTypeSymbol>
    {
        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
        {
            if (x is null || y is null)
                return false;

            return GetKey(x) == GetKey(y);
        }

        public int GetHashCode(INamedTypeSymbol obj)
        {
            return GetKey(obj).GetHashCode();
        }

        private string GetKey(INamedTypeSymbol symbol)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}
