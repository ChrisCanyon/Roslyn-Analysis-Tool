using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GatewayCallGraph;

/// <summary>
/// Two-pass boundary detection. Pass 1 (taint): a method is tainted if its body
/// reaches an <see cref="IoPrimitives"/> leaf, transitively. Pass 2 (surface):
/// when the walker visits a method, we ask the inferrer "is this an intent
/// surface AND is it tainted?" — if yes, tag and stop walking; if it's tainted
/// but not a surface, keep walking and a deeper method may be the surface.
///
/// "Intent surface" is a structural test on the symbol itself:
///   - the method is on an interface, OR
///   - the containing class name ends in one of a small set of suffixes
///     (Gateway, Client, Api, Facade, Service, Repository, Provider, Helper,
///     Wrapper, Factory, Processor, Queue, Queuer)
///
/// User-curated <see cref="BoundarySeeds"/> matches always win — they let us
/// override styling (e.g. "this Service IS a boundary, that one isn't") and
/// promote interfaces we want to surface even when the inferrer wouldn't.
///
/// Walk policy when the deepest tainted node has no surface above the I/O
/// primitive — fall through to the leaf itself. The mechanism shows up rather
/// than the boundary being silently lost.
/// </summary>
public sealed class BoundaryInferrer
{
    private readonly Solution _solution;
    private readonly int _maxDepth;

    // Per-method memoized decision. Computed lazily on first request.
    private readonly Dictionary<IMethodSymbol, Decision> _cache = new(SymbolEqualityComparer.Default);

    // Cycle guard for the recursive taint walk.
    private readonly HashSet<IMethodSymbol> _inProgress = new(SymbolEqualityComparer.Default);

    public BoundaryInferrer(Solution solution, int maxDepth = 10)
    {
        _solution = solution;
        _maxDepth = maxDepth;
    }

    /// <summary>
    /// What we decided about a method.
    /// - Category null: not tainted, just keep walking normally.
    /// - Category set + Surface true: this method is an intent surface, tag and stop.
    /// - Category set + Surface false: tainted but not a surface — keep walking;
    ///   if no callee turns out to be a surface, the leaf primitive itself will
    ///   be tagged the next frame down.
    /// </summary>
    public readonly record struct Decision(string? Category, bool Surface);

    /// <summary>
    /// Get-or-compute the decision for a method. Safe to call repeatedly. Cycle-safe.
    /// </summary>
    public async Task<Decision> ClassifyAsync(IMethodSymbol method)
    {
        method = method.OriginalDefinition;
        if (_cache.TryGetValue(method, out var cached)) return cached;

        // Cycle: assume "no taint" while in progress; the result will be filled
        // in when the outer frame finishes. False negatives on cycles are fine —
        // the cycle has to bottom out at a non-cyclic body anyway.
        if (!_inProgress.Add(method)) return new Decision(null, false);

        try
        {
            var taint = await ComputeTaintAsync(method, depth: 0).ConfigureAwait(false);
            var decision = taint == null
                ? new Decision(null, false)
                : new Decision(taint, IsIntentSurface(method));
            _cache[method] = decision;
            return decision;
        }
        finally
        {
            _inProgress.Remove(method);
        }
    }

    /// <summary>
    /// Walk the method's body. Return the boundary category if the body reaches
    /// an I/O primitive (directly or via any callee), null otherwise.
    ///
    /// When a method touches multiple categories (e.g. an HTTP-egress wrapper
    /// that also reads a config from DB to build the request), we prefer
    /// <see cref="IoPrimitives.ExternalSystemGateway"/>. Reasoning: a *Helper
    /// or *Facade that talks HTTP usually does some DB reads incidentally, but
    /// its *purpose* is the external call. Reversing the priority would tag
    /// HTTP-shaped surfaces as "database_query", which is wrong.
    /// </summary>
    private async Task<string?> ComputeTaintAsync(IMethodSymbol method, int depth)
    {
        if (depth >= _maxDepth) return null;

        // Curated seeds are authoritative — anything in BoundarySeeds.Match is
        // by definition a boundary. We don't need to inspect its body.
        if (BoundarySeeds.Match(
                method.ContainingType?.ToDisplayString() ?? "",
                method.Name) is { } seeded)
        {
            return seeded.Name;
        }

        // I/O primitive itself? Walk the type's base chain so SqlConnection.Open
        // also matches as DbConnection.Open.
        if (TryMatchPrimitive(method) is { } primitiveCategory)
        {
            return primitiveCategory;
        }

        // No body in source = leaf we can't see into. We've already checked the
        // primitive list and the seed list; nothing more to say.
        if (method.DeclaringSyntaxReferences.Length == 0) return null;
        if (method.ContainingType?.DeclaringSyntaxReferences.Length == 0) return null;

        // Track every category seen so we can prefer external_system_gateway
        // over database_query when both appear.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Also: if this method is itself an intent surface (e.g. *Facade) and
        // every meaningful callee disappears into a referenced library we can't
        // see into, that's the "we lost sight" case — tag it as external HTTP.
        // We track whether any out-of-source non-primitive call is made so we
        // can apply this fallback at the end.
        var hasOutOfSourceCall = false;
        var isThisIntentSurface = IsIntentSurface(method);

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntaxNode = await syntaxRef.GetSyntaxAsync().ConfigureAwait(false);
            if (syntaxNode is not BaseMethodDeclarationSyntax decl) continue;

            var bodyNode = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;
            if (bodyNode == null) continue;

            var document = _solution.GetDocument(syntaxNode.SyntaxTree);
            if (document == null) continue;

            var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);
            if (semanticModel == null) continue;

            foreach (var inv in bodyNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(inv);
                var callee = (symbolInfo.Symbol as IMethodSymbol
                              ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault())
                              ?.OriginalDefinition;
                if (callee == null) continue;
                if (SymbolEqualityComparer.Default.Equals(callee, method)) continue; // self

                if (BoundarySeeds.Match(
                        callee.ContainingType?.ToDisplayString() ?? "",
                        callee.Name) is { } seededCallee)
                {
                    seen.Add(seededCallee.Name);
                    continue;
                }

                if (TryMatchPrimitive(callee) is { } primCallee)
                {
                    seen.Add(primCallee);
                    continue;
                }

                // Track out-of-source invocations for the intent-surface fallback,
                // but only when the call is into a "foreign library" — not standard
                // BCL/Newtonsoft/etc. noise. Container ops like List.Add or
                // string.Substring are out-of-source but not external boundaries;
                // counting them would tag every pure-string-parsing method on a
                // *Gateway as external_system_gateway.
                if (callee.ContainingType?.DeclaringSyntaxReferences.Length == 0
                    && IsForeignLibraryCall(callee))
                {
                    hasOutOfSourceCall = true;
                }

                // Recurse — using cache and cycle guard.
                if (_cache.TryGetValue(callee, out var cached))
                {
                    if (cached.Category != null) seen.Add(cached.Category);
                    continue;
                }
                if (_inProgress.Contains(callee)) continue;

                _inProgress.Add(callee);
                try
                {
                    var sub = await ComputeTaintAsync(callee, depth + 1).ConfigureAwait(false);
                    if (sub != null) seen.Add(sub);
                }
                finally
                {
                    _inProgress.Remove(callee);
                }
            }
        }

        // Prefer external HTTP over DB when both are reachable. (See doc comment.)
        if (seen.Contains(IoPrimitives.ExternalSystemGateway)) return IoPrimitives.ExternalSystemGateway;
        if (seen.Count > 0) return seen.First();

        // "Intent surface that wraps an opaque library": if this method is on a
        // *Facade/*Helper/*Api/etc., its body has no in-source taint, and at
        // least one callee disappears into a referenced library we can't walk,
        // assume the library is external HTTP. This catches *Facade-suffixed
        // wrappers that delegate to a NuGet-distributed SDK we can't walk into.
        if (isThisIntentSurface && hasOutOfSourceCall)
        {
            return IoPrimitives.ExternalSystemGateway;
        }

        return null;
    }

    private static string? TryMatchPrimitive(IMethodSymbol method)
    {
        // Walk the containing type's base chain so e.g. SqlConnection.Open is
        // also recognised when the primitive list has DbConnection.Open.
        for (var t = method.ContainingType; t != null; t = t.BaseType)
        {
            var fqn = t.ToDisplayString();
            if (IoPrimitives.Match(fqn, method.Name) is { } cat) return cat;
        }
        // Also check interfaces the containing type implements (covers IDbConnection,
        // IDbCommand, etc.).
        if (method.ContainingType is { } ct)
        {
            foreach (var iface in ct.AllInterfaces)
            {
                var fqn = iface.ToDisplayString();
                if (IoPrimitives.Match(fqn, method.Name) is { } cat) return cat;
            }
        }

        // "Lost sight at an HTTP-shaped boundary": if the call leaves source we
        // can analyze AND the destination type's short name suggests an HTTP
        // client (*Client, *Api, *ApiClient), assume it's an external HTTP egress.
        // This is what catches generated Refit/Swagger clients (e.g. CartClient,
        // TransactionClient) that wrap HttpClient.SendAsync inside a referenced
        // assembly we can't walk into.
        if (method.ContainingType is { } cls
            && cls.DeclaringSyntaxReferences.Length == 0)
        {
            var name = cls.Name;
            if ((name.EndsWith("Client", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("Api", StringComparison.OrdinalIgnoreCase))
                && !name.StartsWith("Http", StringComparison.Ordinal)) // exclude HttpClient itself, already handled by exact match
            {
                return IoPrimitives.ExternalSystemGateway;
            }
        }
        return null;
    }

    /// <summary>
    /// Structural "is this an intent boundary" check. Interfaces always qualify.
    /// Otherwise the containing type's *short* name (last namespace segment) must
    /// end in one of the well-known I/O-suffixes seen in this codebase.
    /// Case-insensitive so e.g. OutageManagementAPI (uppercase) and TsmApi
    /// (camel) both qualify.
    /// </summary>
    private static readonly string[] IntentSuffixes =
    {
        "Gateway", "Client", "Api", "Facade", "Service", "Repository",
        "Provider", "Helper", "Wrapper", "Factory", "Processor", "Queue", "Queuer",
    };

    /// <summary>
    /// Namespace prefixes that are "infrastructure noise" — calls into these
    /// are out-of-source but never represent an external boundary in our model.
    /// Anything OUTSIDE these prefixes is a foreign library worth tracking for
    /// the intent-surface fallback.
    /// </summary>
    private static readonly string[] BclNamespacePrefixes =
    {
        "System.",
        "Microsoft.",
        "Newtonsoft.",
        "log4net.",
        "NLog.",
        "Serilog.",
        "Castle.",
        "FluentAssertions.",
        "AutoMapper.",
    };

    private static bool IsForeignLibraryCall(IMethodSymbol callee)
    {
        var ns = callee.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "";
        if (string.IsNullOrEmpty(ns)) return false;
        foreach (var prefix in BclNamespacePrefixes)
        {
            if (ns.StartsWith(prefix, StringComparison.Ordinal)) return false;
            if (ns == prefix.TrimEnd('.')) return false;
        }
        return true;
    }

    public static bool IsIntentSurface(IMethodSymbol method)
    {
        var ct = method.ContainingType;
        if (ct == null) return false;
        if (ct.TypeKind == TypeKind.Interface) return true;

        var name = ct.Name; // short name, no namespace
        foreach (var suffix in IntentSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
