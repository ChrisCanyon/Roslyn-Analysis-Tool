using GatewayCallGraph;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace MVCWebView.Controllers.Api
{
    /// <summary>
    /// API for the gateway-call-graph UI (rendered by HomeController.CallGraph).
    ///   GET /api/CallGraph/Controllers     - list every controller action in the solution
    ///   GET /api/CallGraph/Graph?fqn=...   - SVG of the down-walk rooted at any in-source method
    ///   GET /api/CallGraph/GraphJson?fqn=...- raw CallGraph JSON for the same root (debug / power users)
    /// </summary>
    [Route("api/[controller]/[action]")]
    public class CallGraphController : Controller
    {
        private readonly ControllerEnumerator _enumerator;
        private readonly ControllerGraphBuilder _builder;
        private readonly IMemoryCache _cache;

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
            IMemoryCache cache)
        {
            _enumerator = enumerator;
            _builder = builder;
            _cache = cache;
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
        /// Builds (or pulls from cache) the full graph rooted at <paramref name="fqn"/>,
        /// then optionally prunes to ancestors+focus+descendants when focus is set.
        /// Boundary call counts are recomputed against whichever graph the caller
        /// gets back (full vs pruned) so the polynomials reflect what's visible.
        /// </summary>
        private async Task<CallGraph?> GetGraphForRequestAsync(string fqn, bool expand, IReadOnlySet<string>? hidden, string? focus)
        {
            var fullGraph = await BuildGraphAsync(fqn, expand, hidden);
            if (fullGraph == null) return null;
            if (string.IsNullOrWhiteSpace(focus)) return fullGraph;

            var pruned = CallGraphFocus.Prune(fullGraph, focus);
            if (pruned == null) return fullGraph; // focus FQN not in graph; fall back to full
            pruned.BoundaryCallCounts = BoundaryCallCountAnalyzer.Compute(pruned);
            return pruned;
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

        private async Task<CallGraph?> BuildGraphAsync(string fqn, bool expandPastBoundaries, IReadOnlySet<string>? hiddenImplFqns)
        {
            var graphKey = $"callgraph.graph::{fqn}::expand={expandPastBoundaries}::hide={HiddenImplsKey(hiddenImplFqns)}";
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

                var graph = await _builder.BuildAsync(rootSymbol, expandPastBoundaries, hiddenImplFqns);
                graph.BoundaryCallCounts = BoundaryCallCountAnalyzer.Compute(graph);
                _cache.Set(graphKey, graph, GraphCacheLifetime);
                return graph;
            }
            finally
            {
                sem.Release();
            }
        }
    }
}
