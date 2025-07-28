using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    /// <summary>
    /// Provides a comparer for <see cref="INamedTypeSymbol"/> that compares symbols by their fully qualified metadata name,
    /// allowing equality checks across different Roslyn compilations.
    /// </summary>
    /// <remarks>
    /// Roslyn symbols from different compilations are not reference-equal, even if they represent the same type.
    /// This comparer normalizes comparison using <c>ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)</c>,
    /// enabling consistent symbol matching across projects and compilations.
    /// </remarks>
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
