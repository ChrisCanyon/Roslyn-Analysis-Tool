using Microsoft.CodeAnalysis;

namespace GatewayCallGraph;

/// <summary>
/// Identity key for an <see cref="IMethodSymbol"/> that survives cross-compilation
/// duplicates. Two distinct <c>IMethodSymbol</c> instances for the same logical
/// method (different project compilations of the same partial class, or the
/// same interface viewed from a different consuming compilation) compare equal
/// here as long as their <see cref="IMethodSymbol.ToDisplayString"/> matches.
///
/// Why: <c>SymbolEqualityComparer.Default</c> says these aren't equal, so any
/// <c>HashSet&lt;IMethodSymbol&gt;</c> or <c>Dictionary&lt;IMethodSymbol, T&gt;</c>
/// keyed on it silently double-counts the same logical method. The graph's
/// node table already worked around this with parallel symbol+fqn indexes;
/// this type lifts the same trick into a reusable value.
///
/// Use it everywhere a method is hashed/keyed: visited sets in walkers,
/// memoization caches in classifiers, dispatch fan-out dedup. The original
/// <see cref="Symbol"/> is preserved for callers that still need to read
/// syntax trees / ContainingType / etc; equality just doesn't depend on it.
/// </summary>
public readonly record struct MethodKey
{
    public IMethodSymbol Symbol { get; }
    public string Fqn { get; }

    private MethodKey(IMethodSymbol symbol, string fqn)
    {
        Symbol = symbol;
        Fqn = fqn;
    }

    /// <summary>
    /// Build a key from a method symbol. Always normalizes to
    /// <see cref="ISymbol.OriginalDefinition"/> so generic constructions and
    /// the open generic compare equal — same behavior the walker and graph
    /// already rely on.
    /// </summary>
    public static MethodKey From(IMethodSymbol method)
    {
        var original = (IMethodSymbol)method.OriginalDefinition;
        return new MethodKey(original, original.ToDisplayString());
    }

    /// <summary>
    /// Symbol-or-FQN equality. We try the cheap symbol comparison first and
    /// fall back to FQN string equality, mirroring the dual-lookup the graph
    /// uses internally. Either match is sufficient.
    /// </summary>
    public bool Equals(MethodKey other)
    {
        if (SymbolEqualityComparer.Default.Equals(Symbol, other.Symbol)) return true;
        return string.Equals(Fqn, other.Fqn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hash on FQN only — symbol hash codes are unstable across compilations
    /// so they can't anchor the bucket. Two MethodKeys that compare equal
    /// MUST share a hash, and FQN equality is the cross-compilation guarantee.
    /// </summary>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fqn);
}
