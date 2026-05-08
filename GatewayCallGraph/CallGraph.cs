using Microsoft.CodeAnalysis;
using System.Text.Json.Serialization;

namespace GatewayCallGraph;

/// <summary>
/// Walker-internal kind of a node. A node also carries an optional
/// <see cref="GraphNode.BoundaryCategory"/> name when it matches a user-defined
/// boundary; the walker terminates at boundary nodes regardless of <see cref="NodeKind"/>.
///
/// Priority for promotion is Boundary > ControllerAction > Intermediate, but
/// since Boundary is now expressed via a separate field, NodeKind only ranks
/// ControllerAction over Intermediate.
/// </summary>
public enum NodeKind
{
    Intermediate,
    ControllerAction,
}

/// <summary>
/// What kind of loop a call site is enclosed in, if any.
/// statement_loop = for/foreach/while/do.
/// enumerable_loop = inside a lambda passed to a System.Linq enumerable method (Select, Where, ForEach, etc.)
/// </summary>
public enum LoopKind
{
    None,
    StatementLoop,
    EnumerableLoop,
}

/// <summary>
/// Distinguishes the syntactic shape of an enclosing conditional. We collapse
/// "else if" into <see cref="If"/> with the chained condition; "else" branches
/// without a condition use <see cref="Else"/>; ternaries use <see cref="Ternary"/>.
/// </summary>
public enum ConditionalKind
{
    None,
    If,
    Else,
    Ternary,
}

public sealed record ConditionalInfo
{
    [JsonPropertyName("kind")]
    public ConditionalKind Kind { get; init; }

    /// <summary>
    /// Truncated, whitespace-normalized text of the condition. For an
    /// <see cref="ConditionalKind.If"/> this is the predicate (e.g.
    /// <c>"x != null && x.Count > 0"</c>). For <see cref="ConditionalKind.Else"/>
    /// it is empty (the `else` branch has no condition of its own; the negation
    /// of the parent `if` is implied). For <see cref="ConditionalKind.Ternary"/>
    /// it is the condition expression of the `?:` operator. Truncated to keep
    /// edge labels readable.
    /// </summary>
    [JsonPropertyName("condition")]
    public string Condition { get; init; } = "";

    [JsonPropertyName("file")]
    public string? File { get; init; }

    /// <summary>Line of the if/else/ternary keyword (so users can jump to it).</summary>
    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>
    /// True if the call sits in the `else` arm of the enclosing if. We carry
    /// this as a flag rather than synthesizing "!(cond)" text because the raw
    /// condition is already in <see cref="Condition"/> and inverting it
    /// programmatically is misleading for non-boolean predicates.
    /// </summary>
    [JsonPropertyName("isElseBranch")]
    public bool IsElseBranch { get; init; }
}

public sealed record LoopInfo
{
    [JsonPropertyName("kind")]
    public LoopKind Kind { get; init; }

    /// <summary>e.g. "for", "foreach", "while", "do" for statement_loop;
    /// or the LINQ method name (e.g. "Select", "ForEach") for enumerable_loop.</summary>
    [JsonPropertyName("subkind")]
    public string Subkind { get; init; } = "";

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    /// <summary>
    /// What the loop is iterating, when we can name it cheaply from syntax:
    /// <list type="bullet">
    ///   <item>foreach: the enumerable expression text (e.g. <c>"accounts"</c>,
    ///     <c>"customer.Payments"</c>, <c>"db.Orders.Where(o =&gt; o.Open)"</c>).</item>
    ///   <item>LINQ enumerable: the receiver expression (e.g. <c>"accounts"</c>
    ///     for <c>accounts.Select(...)</c>).</item>
    ///   <item>for / while / do: the condition text — not "what we're enumerating"
    ///     but the closest analog and useful context for the reader.</item>
    /// </list>
    /// Truncated to keep edge labels readable. Null when the construct doesn't
    /// have a meaningful single source expression (e.g. an empty <c>for(;;)</c>).
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>
    /// Total count of loops enclosing the call site, between it and the
    /// containing method body. Depth=1 is "inside one loop"; depth=2 is
    /// "inside a nested loop." Used by the side panel to compute call-count
    /// formulas like 2N + N^2.
    /// </summary>
    [JsonPropertyName("depth")]
    public int Depth { get; init; }
}

public sealed class CallSite
{
    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }
}

public sealed record GraphNode
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("fqn")]
    public string Fqn { get; init; } = "";

    [JsonPropertyName("type")]
    public string TypeName { get; init; } = "";

    [JsonPropertyName("method")]
    public string MethodName { get; init; } = "";

    [JsonPropertyName("kind")]
    public NodeKind Kind { get; init; }

    /// <summary>
    /// If this node matches a user-defined <see cref="BoundaryCategory"/>, the
    /// category's stable name (e.g. "external_system_gateway"). Null otherwise.
    /// Boundary nodes terminate the walk and are styled by the renderer using
    /// the category's NodeStyle.
    /// </summary>
    [JsonPropertyName("boundary")]
    public string? BoundaryName { get; init; }

    [JsonIgnore]
    public IMethodSymbol Symbol { get; init; } = null!;
}

public sealed class GraphEdge
{
    /// <summary>caller node id (the method that contains the call site)</summary>
    [JsonPropertyName("from")]
    public int From { get; init; }

    /// <summary>callee node id (the method being called)</summary>
    [JsonPropertyName("to")]
    public int To { get; init; }

    [JsonPropertyName("callSite")]
    public CallSite CallSite { get; init; } = new();

    /// <summary>null if the call site is not inside any loop.</summary>
    [JsonPropertyName("loop")]
    public LoopInfo? Loop { get; init; }

    /// <summary>
    /// null if the call site is not inside any if/else/ternary. Independent of
    /// <see cref="Loop"/> — a call can be inside both a loop and a conditional.
    /// </summary>
    [JsonPropertyName("conditional")]
    public ConditionalInfo? Conditional { get; init; }

    /// <summary>
    /// True for synthetic edges that connect an interface method to one of its
    /// concrete implementations (virtual dispatch). These edges have no real
    /// call site of their own; the call site is the edge that lands at the
    /// interface node. Renderers should style these distinctly (e.g. dashed grey).
    /// </summary>
    [JsonPropertyName("dispatch")]
    public bool Dispatch { get; init; }

    /// <summary>
    /// Only set on dispatch edges. The full FQNs of every concrete in-source
    /// class whose vtable resolves through this dispatch edge for the
    /// from-node's method.
    ///
    /// Crucial for the per-interface picker: when the user picks "use Munis
    /// for IUtilityBillingGateway," methods Munis OVERRIDES route through
    /// <c>Munis.M</c> (so its dispatch edge has <c>Munis</c> in
    /// ServesTypes), but methods Munis INHERITS route through
    /// <c>RestApi.M</c> (so its dispatch edge has BOTH <c>RestApi</c> AND
    /// <c>Munis</c> in ServesTypes — Munis's runtime path goes through
    /// the inherited base impl). The picker filter rule is then
    /// "keep dispatch edges whose ServesTypes contains the picked concrete
    /// type; hide the rest" — automatically correct for both override and
    /// inheritance cases.
    ///
    /// Null for non-dispatch edges.
    /// </summary>
    [JsonPropertyName("servesTypes")]
    public List<string>? ServesTypes { get; init; }
}

/// <summary>
/// Snapshot of a boundary category emitted alongside the graph so consumers
/// (the front-end JS, downstream tooling) know how to style nodes tagged with
/// that category without having to hardcode it.
/// </summary>
public sealed record BoundaryCategoryInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("style")] NodeStyleInfo Style);

public sealed record NodeStyleInfo(
    [property: JsonPropertyName("shape")] string Shape,
    [property: JsonPropertyName("fillColor")] string FillColor,
    [property: JsonPropertyName("strokeColor")] string StrokeColor,
    [property: JsonPropertyName("style")] string Style,
    [property: JsonPropertyName("fontColor")] string FontColor);

/// <summary>
/// Stable per-controller snapshot of "what concrete classes can serve as
/// implementations for each interface or virtual-base type with dispatch
/// fan-out in this controller's maximal call graph."
///
/// Computed ONCE per maximal graph (in <see cref="DispatchServesTypesResolver"/>),
/// then carried unchanged through every post-pass prune (collapse, hide,
/// focus). The picker UI reads from this so the dropdown options stay
/// stable across navigation: focusing a node or picking an impl doesn't
/// change which interfaces are offered or which classes you can pick from.
/// </summary>
public sealed record InterfaceImplsInfo(
    [property: JsonPropertyName("typeFqn")] string TypeFqn,
    [property: JsonPropertyName("concretes")] List<string> Concretes);

public sealed class CallGraph
{
    [JsonPropertyName("nodes")]
    public List<GraphNode> Nodes { get; } = new();

    [JsonPropertyName("edges")]
    public List<GraphEdge> Edges { get; } = new();

    /// <summary>
    /// Boundary categories present in this graph (only those with at least one
    /// node tagged). Renderers and front-ends use this to pick up styling.
    /// </summary>
    [JsonPropertyName("boundaries")]
    public List<BoundaryCategoryInfo> Boundaries { get; } = new();

    /// <summary>
    /// Per-boundary call-count polynomials, one per boundary node, computed
    /// path-aware from the controller root. Populated by callers via
    /// <see cref="BoundaryCallCountAnalyzer.Compute"/> after the graph is built;
    /// not maintained incrementally as edges are added. Sorted heaviest first.
    /// </summary>
    [JsonPropertyName("boundaryCallCounts")]
    public List<BoundaryCallCount> BoundaryCallCounts { get; set; } = new();

    /// <summary>
    /// Stable per-controller picker options. Populated once during build by
    /// <see cref="DispatchServesTypesResolver"/>; carried unchanged through
    /// every post-pass prune. The picker UI uses this so the available
    /// interfaces and concrete-class options don't change when the user
    /// focuses, hides, or collapses — same controller means same options.
    /// </summary>
    [JsonPropertyName("interfaceImpls")]
    public List<InterfaceImplsInfo> InterfaceImpls { get; set; } = new();

    // Two parallel indexes. The symbol map is the fast path — Roslyn usually
    // hands out the same IMethodSymbol instance for the same method, so
    // SymbolEqualityComparer hits in O(1). The fqn map catches the cases it
    // misses: partial classes, methods reached from different compilations,
    // or any other path where Roslyn produces two distinct IMethodSymbol
    // instances for the *same logical method*. Without this, Account.GetBalance
    // can show up as two nodes when called via two different code paths.
    private readonly Dictionary<IMethodSymbol, int> _idBySymbol = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, int> _idByFqn = new(StringComparer.Ordinal);
    private readonly HashSet<(int from, int to, string? file, int line)> _edgeKeys = new();
    private readonly HashSet<string> _boundariesPresent = new(StringComparer.Ordinal);
    private int _nextId = 0;

    /// <summary>
    /// Gets or creates a node id for the given method symbol. NodeKind defaults to
    /// Intermediate; callers can promote a node to ControllerAction or GatewayMethod
    /// via <see cref="PromoteKind"/>.
    /// </summary>
    public int GetOrAddNode(IMethodSymbol method)
    {
        if (_idBySymbol.TryGetValue(method, out var existing)) return existing;

        // Fall back to fqn lookup so two distinct IMethodSymbol instances for
        // the same method (partial classes, cross-compilation, etc.) collapse
        // to a single node. Cache the symbol -> id mapping for next time so we
        // don't keep paying the fqn-string cost.
        var fqn = method.ToDisplayString();
        if (_idByFqn.TryGetValue(fqn, out var existingByFqn))
        {
            _idBySymbol[method] = existingByFqn;
            return existingByFqn;
        }

        var id = _nextId++;
        _idBySymbol[method] = id;
        _idByFqn[fqn] = id;
        Nodes.Add(new GraphNode
        {
            Id = id,
            Fqn = fqn,
            TypeName = method.ContainingType?.ToDisplayString() ?? "",
            MethodName = method.Name,
            Kind = NodeKind.Intermediate,
            Symbol = method,
        });
        return id;
    }

    public void PromoteKind(int nodeId, NodeKind kind)
    {
        var existing = Nodes[nodeId];
        // controller > intermediate (boundary is a separate field, not a kind)
        if ((int)kind > (int)existing.Kind)
        {
            Nodes[nodeId] = existing with { Kind = kind };
        }
    }

    /// <summary>
    /// Tags a node as belonging to a user-defined boundary category. Idempotent —
    /// a node already tagged with the same category is a no-op; tagging a node
    /// with a different category replaces the previous tag (caller is responsible
    /// for ordering — typically the more specific concrete-class match wins).
    /// Also records the category snapshot in the graph's boundary list (once)
    /// so renderers can look up the style.
    /// </summary>
    public void TagBoundary(int nodeId, BoundaryCategory category)
    {
        var existing = Nodes[nodeId];
        if (existing.BoundaryName != category.Name)
        {
            Nodes[nodeId] = existing with { BoundaryName = category.Name };
        }
        if (_boundariesPresent.Add(category.Name))
        {
            Boundaries.Add(new BoundaryCategoryInfo(
                Name: category.Name,
                DisplayName: category.DisplayName,
                Style: new NodeStyleInfo(
                    Shape: category.Style.Shape,
                    FillColor: category.Style.FillColor,
                    StrokeColor: category.Style.StrokeColor,
                    Style: category.Style.Style,
                    FontColor: category.Style.FontColor)));
        }
    }

    /// <summary>
    /// Adds an edge if not already present. Uniqueness key is (from, to, callSite.file, callSite.line)
    /// so the same caller→callee relationship at multiple distinct call sites produces multiple edges.
    /// </summary>
    public void AddEdge(int from, int to, CallSite site, LoopInfo? loop, ConditionalInfo? conditional = null)
    {
        var key = (from, to, site.File, site.Line);
        if (!_edgeKeys.Add(key)) return;
        Edges.Add(new GraphEdge
        {
            From = from,
            To = to,
            CallSite = site,
            Loop = loop,
            Conditional = conditional,
        });
    }

    /// <summary>
    /// Adds a synthetic dispatch edge from an interface (or virtual) method to one
    /// of its implementations. At most one dispatch edge per (from, to) pair; the
    /// real call site lives on the edge that targets the interface itself.
    /// </summary>
    public void AddDispatchEdge(int from, int to)
    {
        var key = (from, to, (string?)"<dispatch>", 0);
        if (!_edgeKeys.Add(key)) return;
        Edges.Add(new GraphEdge
        {
            From = from,
            To = to,
            CallSite = new CallSite(),
            Loop = null,
            Dispatch = true,
        });
    }

    /// <summary>
    /// Set the <see cref="GraphEdge.ServesTypes"/> annotation on the dispatch
    /// edge between <paramref name="from"/> and <paramref name="to"/>. Called
    /// by the post-build resolver. Replaces any existing list.
    /// </summary>
    public void SetDispatchServesTypes(int from, int to, IReadOnlyCollection<string> servesTypes)
    {
        for (var i = 0; i < Edges.Count; i++)
        {
            var e = Edges[i];
            if (e.From != from || e.To != to || !e.Dispatch) continue;
            var ordered = servesTypes
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            Edges[i] = new GraphEdge
            {
                From = e.From,
                To = e.To,
                CallSite = e.CallSite,
                Loop = e.Loop,
                Conditional = e.Conditional,
                Dispatch = e.Dispatch,
                ServesTypes = ordered.Count == 0 ? null : ordered,
            };
            return;
        }
    }
}
