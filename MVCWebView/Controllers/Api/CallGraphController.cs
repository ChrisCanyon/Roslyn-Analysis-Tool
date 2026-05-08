using System.Text;
using System.Text.Json;
using GatewayCallGraph;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MVCWebView.Controllers.Api
{
    /// <summary>
    /// API for the gateway-call-graph UI (rendered by HomeController.CallGraph).
    ///   GET  /api/CallGraph/Controllers     - list every controller action in the solution
    ///   GET  /api/CallGraph/Graph?fqn=...   - SVG of the down-walk rooted at any in-source method
    ///   GET  /api/CallGraph/GraphJson?fqn=...- raw CallGraph JSON for the same root (debug / power users)
    ///   POST /api/CallGraph/Export?fqn=...  - dump SVG + JSON + meta to disk for offline analysis
    /// </summary>
    [Route("api/[controller]/[action]")]
    public class CallGraphController : Controller
    {
        private readonly ControllerEnumerator _enumerator;
        private readonly ControllerGraphBuilder _builder;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly IWebHostEnvironment _env;

        // SVG renders and built graphs are cached. Solution lifetime ==
        // process lifetime, so we don't need to invalidate.
        private static readonly TimeSpan SvgCacheLifetime = TimeSpan.FromHours(2);
        private static readonly TimeSpan GraphCacheLifetime = TimeSpan.FromHours(2);

        // Per-key build locks: ensures the parallel Graph + GraphJson fetches
        // for the same key build the underlying graph exactly once. Without
        // this they race on the shared BoundaryInferrer state.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> BuildLocks = new();

        public CallGraphController(
            ControllerEnumerator enumerator,
            ControllerGraphBuilder builder,
            IMemoryCache cache,
            IConfiguration config,
            IWebHostEnvironment env)
        {
            _enumerator = enumerator;
            _builder = builder;
            _cache = cache;
            _config = config;
            _env = env;
        }

        [HttpGet]
        public async Task<IActionResult> Controllers()
        {
            const string cacheKey = "callgraph.controllers";
            if (!_cache.TryGetValue(cacheKey, out List<ControllerEnumerator.ControllerActionInfo>? cached))
            {
                cached = await _enumerator.ListAsync();
                _cache.Set(cacheKey, cached, TimeSpan.FromHours(8));
            }
            return Json(cached);
        }

        [HttpGet]
        public async Task<IActionResult> Graph(string fqn, bool expand = false, string? hideImpls = null, string? focus = null)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return BadRequest("fqn required");

            var hidden = ParseHiddenImpls(hideImpls);
            var focusKey = string.IsNullOrWhiteSpace(focus) ? "" : focus;
            // Cache key includes every parameter that changes the rendered SVG.
            var cacheKey = $"callgraph.svg::{fqn}::expand={expand}::hide={HiddenImplsKey(hidden)}::focus={focusKey}";
            if (!_cache.TryGetValue(cacheKey, out string? svg))
            {
                var graph = await GetGraphForRequestAsync(fqn, expand, hidden, focus);
                if (graph == null) return NotFound($"Method not found: {fqn}");
                svg = await GraphvizRenderer.RenderSvgAsync(graph);
                _cache.Set(cacheKey, svg, SvgCacheLifetime);
            }
            return Content(svg!, "image/svg+xml");
        }

        [HttpGet]
        public async Task<IActionResult> GraphJson(string fqn, bool expand = false, string? hideImpls = null, string? focus = null)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return BadRequest("fqn required");
            var hidden = ParseHiddenImpls(hideImpls);
            var graph = await GetGraphForRequestAsync(fqn, expand, hidden, focus);
            if (graph == null) return NotFound($"Method not found: {fqn}");
            return Json(graph);
        }

        /// <summary>
        /// Pull (or build) the maximal canonical graph for <paramref name="fqn"/>,
        /// then apply view-time prunes in this order:
        ///   1. Collapse-at-boundary (skipped when <paramref name="expand"/> is true).
        ///   2. Hide impls — drops user-deselected impl FQNs and orphan subtrees.
        ///   3. Focus — narrows to ancestors + focus + descendants of the picked node.
        ///
        /// Polynomials get recomputed against the final pruned graph so the
        /// numbers always reflect what's actually visible. Each prune is a
        /// pure graph→graph transform; none triggers a Roslyn rebuild.
        ///
        /// Order rationale: hide-impls runs AFTER collapse so a user picking
        /// "show me Munis" doesn't accidentally bring back nodes the collapse
        /// just removed. Focus runs LAST so it narrows whatever the user has
        /// shaped via the previous toggles, not the maximal graph.
        /// </summary>
        private async Task<CallGraph?> GetGraphForRequestAsync(string fqn, bool expand, IReadOnlySet<string>? hidden, string? focus)
        {
            var fullGraph = await BuildGraphAsync(fqn);
            if (fullGraph == null) return null;

            var view = expand ? fullGraph : CallGraphCollapseBoundaries.Prune(fullGraph);
            view = CallGraphHideImpls.Prune(view, hidden);
            if (!string.IsNullOrWhiteSpace(focus))
            {
                view = CallGraphFocus.Prune(view, focus) ?? view;
            }

            // If anything got pruned (object identity differs from the cached
            // full graph), recompute polynomials against the visible subgraph.
            // When nothing was pruned we keep the cached BoundaryCallCounts as-is.
            if (!ReferenceEquals(view, fullGraph))
            {
                view.BoundaryCallCounts = BoundaryCallCountAnalyzer.Compute(view);
            }
            return view;
        }

        /// <summary>
        /// hideImpls is a pipe-separated list of FQNs (URL-encoded). Returns null
        /// when the input is empty so the builder can short-circuit the filter.
        /// </summary>
        private static IReadOnlySet<string>? ParseHiddenImpls(string? hideImpls)
        {
            if (string.IsNullOrWhiteSpace(hideImpls)) return null;
            var parts = hideImpls.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? null : new HashSet<string>(parts, StringComparer.Ordinal);
        }

        private static string HiddenImplsKey(IReadOnlySet<string>? hidden)
        {
            if (hidden == null || hidden.Count == 0) return "";
            return string.Join("|", hidden.OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>
        /// Build (or pull from cache) the maximal canonical graph for the given
        /// root method. Cache key is the FQN alone — the walker no longer takes
        /// any view-mode parameters, so one Roslyn walk produces the artifact
        /// for every (expand, hide, focus) view derived from it.
        /// </summary>
        private async Task<CallGraph?> BuildGraphAsync(string fqn)
        {
            var graphKey = $"callgraph.graph::{fqn}";
            if (_cache.TryGetValue(graphKey, out CallGraph? cached)) return cached;

            // Per-key lock so the parallel Graph + GraphJson on the same key
            // produce one build, not two racing on the shared inferrer state.
            var sem = BuildLocks.GetOrAdd(graphKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                if (_cache.TryGetValue(graphKey, out cached)) return cached;

                var rootSymbol = await _enumerator.ResolveAnyMethodAsync(fqn);
                if (rootSymbol == null) return null;

                var graph = await _builder.BuildAsync(rootSymbol);
                graph.BoundaryCallCounts = BoundaryCallCountAnalyzer.Compute(graph);
                _cache.Set(graphKey, graph, GraphCacheLifetime);
                return graph;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// Dump the current view (SVG + raw JSON + metadata + a notes scratchpad)
        /// to disk for offline analysis. The export captures exactly what the
        /// user sees: same fqn, expand toggle, hidden impls, and focus.
        ///
        /// Folder layout under <c>ExportDirectory</c> (default
        /// <c>&lt;repo&gt;/audit/exports</c>, gitignored):
        ///   yyyyMMdd-HHmmss_{Class.Method}[_focus-{Class.Method}]/
        ///     graph.svg   — rendered SVG identical to /Graph response
        ///     graph.json  — CallGraph payload identical to /GraphJson response
        ///     meta.json   — root fqn, focus, expand, hidden impls, timestamp
        ///     notes.md    — empty scratchpad seeded with root + focus headers
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Export(string fqn, bool expand = false, string? hideImpls = null, string? focus = null)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return BadRequest("fqn required");

            var hidden = ParseHiddenImpls(hideImpls);
            var graph = await GetGraphForRequestAsync(fqn, expand, hidden, focus);
            if (graph == null) return NotFound($"Method not found: {fqn}");
            var svg = await GraphvizRenderer.RenderSvgAsync(graph);

            var exportDir = ResolveExportDirectory();
            var folderName = BuildFolderName(fqn, focus);
            var folderPath = Path.Combine(exportDir, folderName);
            Directory.CreateDirectory(folderPath);

            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
            };

            await System.IO.File.WriteAllTextAsync(Path.Combine(folderPath, "graph.svg"), svg, Encoding.UTF8);
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(folderPath, "graph.json"),
                JsonSerializer.Serialize(graph, jsonOpts),
                Encoding.UTF8);

            var meta = new
            {
                rootFqn = fqn,
                focus = string.IsNullOrWhiteSpace(focus) ? null : focus,
                expandPastBoundaries = expand,
                hiddenImpls = hidden?.OrderBy(s => s, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>(),
                exportedAtUtc = DateTime.UtcNow.ToString("o"),
                solutionPath = _config["SolutionPath"],
            };
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(folderPath, "meta.json"),
                JsonSerializer.Serialize(meta, jsonOpts),
                Encoding.UTF8);

            // Empty scratchpad for the human to drop "I think this is cached
            // via Lazy<T> in AccountModel — please verify" notes before handing
            // the folder to a reviewer / agent.
            var notesPath = Path.Combine(folderPath, "notes.md");
            if (!System.IO.File.Exists(notesPath))
            {
                await System.IO.File.WriteAllTextAsync(notesPath,
                    $"# Notes\n\nRoot: `{fqn}`\nFocus: `{focus ?? "(none)"}`\n\n",
                    Encoding.UTF8);
            }

            return Json(new { folder = folderPath });
        }

        /// <summary>
        /// Resolve the export directory, in order:
        ///   1. ExportDirectory in config (appsettings.local.json or env override)
        ///   2. {ContentRoot}/../audit/exports — the gitignored repo location
        /// </summary>
        private string ResolveExportDirectory()
        {
            var configured = _config["ExportDirectory"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.IsPathRooted(configured)
                    ? configured
                    : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configured));
            }
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "audit", "exports"));
        }

        /// <summary>
        /// Folder name pattern: yyyyMMdd-HHmmss_{ShortRoot}[_focus-{ShortFocus}].
        /// The last two FQN segments keep the path readable on disk without
        /// burying it under a 200-char namespace.
        /// </summary>
        private static string BuildFolderName(string rootFqn, string? focus)
        {
            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var rootShort = ShortName(rootFqn);
            var focusShort = string.IsNullOrWhiteSpace(focus) ? null : ShortName(focus);
            var name = focusShort != null
                ? $"{ts}_{rootShort}_focus-{focusShort}"
                : $"{ts}_{rootShort}";
            return SanitizeFolderName(name);
        }

        private static string ShortName(string fqn)
        {
            // FQN looks like "Namespace.Class.Method(args)". Strip parens and
            // grab the trailing two dot-segments: Class.Method.
            var noParens = fqn.Split('(', 2)[0];
            var parts = noParens.Split('.');
            return parts.Length >= 2
                ? $"{parts[^2]}.{parts[^1]}"
                : noParens;
        }

        private static string SanitizeFolderName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(invalid.Contains(ch) || ch == ' ' ? '-' : ch);
            }
            return sb.ToString();
        }
    }
}
