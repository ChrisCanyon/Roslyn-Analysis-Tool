using System.Text.Json;
using System.Text.RegularExpressions;

namespace GatewayCallGraph;

/// <summary>
/// How a node belonging to a particular <see cref="BoundaryCategory"/> should be
/// drawn. Maps directly onto Graphviz attributes — change a value, change the look.
/// </summary>
public sealed record NodeStyle(
    string Shape,
    string FillColor,
    string StrokeColor,
    string Style = "filled,bold",
    string FontColor = "black");

/// <summary>
/// Regex-based matcher for whole families of methods. Both patterns are .NET
/// regexes that are auto-anchored (^...$) at compile time so users don't have
/// to remember to anchor — "Repository" matches the class "Foo.BarRepository"
/// only if it's the entire type display string, NOT a substring. Use ".*Repository"
/// to mean "any class whose name ends with Repository."
///
/// MethodPattern defaults to ".*" — match any method on a matching type.
/// </summary>
public sealed record BoundaryMatcher(
    string TypePattern,
    string MethodPattern = ".*");

/// <summary>
/// A user-defined boundary at which the call-graph walker stops. Each category
/// supplies its own membership list (which (type, method) pairs belong) and its
/// own visual style.
///
/// Matching is exact-first, pattern-fallback:
///   1. <see cref="Members"/> is an O(1) hash lookup. Use this for known specific
///      methods (e.g. every concrete method on a particular gateway).
///   2. <see cref="Patterns"/> is checked only on hash miss. Use this for whole
///      families like ".*Repository" or ".*DbContext".
/// </summary>
public sealed record BoundaryCategory(
    string Name,
    string DisplayName,
    NodeStyle Style,
    IReadOnlyList<(string TypeFullName, string MethodName)> Members,
    IReadOnlyList<BoundaryMatcher>? Patterns = null);

/// <summary>
/// Boundary categories the walker recognizes. Two parts:
///   - <b>Category metadata</b> (name, display name, style, regex patterns) lives
///     in code below. Generic and safe to commit.
///   - <b>Exact <c>Members</c></b> — the curated list of internal type FQNs that
///     should be tagged as boundaries — is loaded at startup from
///     <c>boundary-seeds.local.json</c>, which is .gitignored. Keeps customer-
///     specific class names out of source control.
///
/// If <c>boundary-seeds.local.json</c> is missing the analyzer still works; the
/// regex <c>Patterns</c> (e.g. <c>.*Repository</c>) cover the bulk of cases. The
/// inferrer (BoundaryInferrer) also catches surfaces by suffix without any seed
/// at all. Hand-curated members are an override layer for cases the inference
/// can't reach.
/// </summary>
public static class BoundarySeeds
{
    private const string LocalSeedFileName = "boundary-seeds.local.json";

    private static readonly NodeStyle ExternalSystemGatewayStyle = new(
        Shape: "box",
        FillColor: "#ffd1d1",
        StrokeColor: "#c62828",
        Style: "filled,bold");

    private static readonly NodeStyle DatabaseQueryStyle = new(
        Shape: "cylinder",
        FillColor: "#cfe8ff",
        StrokeColor: "#1565c0",
        Style: "filled,bold");

    /// <summary>
    /// Visual: hex-shape, purple. Used when a boundary touches both HTTP and
    /// DB downstream so the leaf reads "this hits the network AND a database"
    /// at a glance. Matches the purple "leads to mixed" edge color so the
    /// path's color flows into the leaf's color.
    /// </summary>
    private static readonly NodeStyle MixedIoStyle = new(
        Shape: "hexagon",
        FillColor: "#e1bee7",
        StrokeColor: "#6a1b9a",
        Style: "filled,bold");

    /// <summary>
    /// All registered boundary categories. Initialized lazily on first access so
    /// loading the JSON file happens once.
    /// </summary>
    public static IReadOnlyList<BoundaryCategory> All => _all.Value;

    private static readonly Lazy<IReadOnlyList<BoundaryCategory>> _all = new(BuildAll);

    private static IReadOnlyList<BoundaryCategory> BuildAll()
    {
        var membersByCategory = LoadLocalMembers();

        var externalSystemGateway = new BoundaryCategory(
            Name: "external_system_gateway",
            DisplayName: "External System Gateway",
            Style: ExternalSystemGatewayStyle,
            Members: membersByCategory.GetValueOrDefault("external_system_gateway", new List<(string, string)>()));

        var databaseQuery = new BoundaryCategory(
            Name: "database_query",
            DisplayName: "Database Query",
            Style: DatabaseQueryStyle,
            Members: membersByCategory.GetValueOrDefault("database_query", new List<(string, string)>()),
            // Match any class whose FQN ends with "Repository" — picks up most
            // data-access classes without needing an exact-member entry.
            Patterns: new[] { new BoundaryMatcher(TypePattern: @".*Repository", MethodPattern: ".*") });

        var mixedIo = new BoundaryCategory(
            Name: "mixed_io",
            DisplayName: "Mixed I/O (DB + External)",
            Style: MixedIoStyle,
            Members: Array.Empty<(string, string)>());

        return new[] { externalSystemGateway, databaseQuery, mixedIo };
    }

    /// <summary>
    /// Read <c>boundary-seeds.local.json</c> from the assembly directory if it
    /// exists. Returns an empty dict otherwise — the analyzer falls back to
    /// regex patterns and the inferrer.
    ///
    /// Schema:
    ///   {
    ///     "external_system_gateway": [
    ///       { "type": "Foo.IBarGateway", "method": "DoThing" },
    ///       ...
    ///     ],
    ///     "database_query": [ ... ]
    ///   }
    /// </summary>
    private static Dictionary<string, List<(string TypeFullName, string MethodName)>> LoadLocalMembers()
    {
        var result = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);

        var path = ResolveLocalSeedPath();
        if (path == null) return result;

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("_")) continue; // skip _comment etc.
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;

                var list = new List<(string, string)>();
                foreach (var entry in prop.Value.EnumerateArray())
                {
                    if (!entry.TryGetProperty("type", out var t)) continue;
                    if (!entry.TryGetProperty("method", out var m)) continue;
                    var typeStr = t.GetString();
                    var methodStr = m.GetString();
                    if (string.IsNullOrEmpty(typeStr) || string.IsNullOrEmpty(methodStr)) continue;
                    list.Add((typeStr, methodStr));
                }
                result[prop.Name] = list;
            }
        }
        catch (Exception ex)
        {
            // Don't bring down the analyzer over a malformed local file. Print to
            // stderr so devs notice if their seeds didn't load.
            Console.Error.WriteLine($"[BoundarySeeds] Failed to load {path}: {ex.Message}. Continuing with regex-only patterns.");
        }

        return result;
    }

    private static string? ResolveLocalSeedPath()
    {
        // Look next to the executing assembly first; fall back to current dir.
        // (Web app and console tools both end up here.)
        var assemblyDir = Path.GetDirectoryName(typeof(BoundarySeeds).Assembly.Location);
        if (assemblyDir != null)
        {
            var candidate = Path.Combine(assemblyDir, LocalSeedFileName);
            if (File.Exists(candidate)) return candidate;
        }
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), LocalSeedFileName);
        if (File.Exists(cwd)) return cwd;
        return null;
    }

    /// <summary>
    /// Pre-computed exact-match lookup: (TypeFullName, MethodName) -> BoundaryCategory.
    /// O(1). Use <see cref="Match"/> for the full exact-then-regex matching.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<(string, string), BoundaryCategory>> _exactLookup = new(() =>
        All.SelectMany(cat => cat.Members.Select(m => (Member: m, Category: cat)))
           .GroupBy(x => x.Member)
           .ToDictionary(g => g.Key, g => g.First().Category));

    /// <summary>
    /// Pre-compiled regex matchers, paired with their owning category. Iterated
    /// linearly on each lookup miss; first match wins (categories declared
    /// earlier in <see cref="All"/> have priority).
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(Regex TypeRx, Regex MethodRx, BoundaryCategory Category)>> _compiledPatterns = new(() =>
        All.SelectMany(cat => (cat.Patterns ?? Array.Empty<BoundaryMatcher>())
                .Select(p => (
                    TypeRx: BuildAnchored(p.TypePattern),
                    MethodRx: BuildAnchored(p.MethodPattern),
                    Category: cat)))
           .ToList());

    private static Regex BuildAnchored(string pattern)
    {
        // Auto-anchor with ^...$ so users don't have to remember. Compile for
        // the hot-path scan. CultureInvariant because type/method names are
        // ASCII identifiers. IgnoreCase so .*Api matches both API and Api.
        var anchored = pattern.StartsWith("^") || pattern.EndsWith("$")
            ? pattern
            : $"^{pattern}$";
        return new Regex(anchored, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Resolve which boundary category, if any, a given (typeFqn, methodName)
    /// belongs to. Exact members win over patterns; among patterns, the first
    /// match in declaration order wins.
    /// </summary>
    public static BoundaryCategory? Match(string typeFqn, string methodName)
    {
        if (_exactLookup.Value.TryGetValue((typeFqn, methodName), out var exact)) return exact;
        foreach (var (typeRx, methodRx, cat) in _compiledPatterns.Value)
        {
            if (typeRx.IsMatch(typeFqn) && methodRx.IsMatch(methodName)) return cat;
        }
        return null;
    }

    /// <summary>
    /// Look up a boundary category by its stable name (e.g. "external_system_gateway").
    /// Used by the inferrer to translate a category string back into the
    /// full <see cref="BoundaryCategory"/> for tagging.
    /// </summary>
    public static BoundaryCategory? GetByName(string name)
    {
        foreach (var cat in All)
        {
            if (cat.Name == name) return cat;
        }
        return null;
    }
}
