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

        // SVG renders are cached by FQN. Solution lifetime == process lifetime,
        // so we don't need to invalidate.
        private static readonly TimeSpan SvgCacheLifetime = TimeSpan.FromHours(2);

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
        public async Task<IActionResult> Graph(string fqn)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return BadRequest("fqn required");

            var cacheKey = $"callgraph.svg::{fqn}";
            if (!_cache.TryGetValue(cacheKey, out string? svg))
            {
                var graph = await BuildGraphAsync(fqn);
                if (graph == null) return NotFound($"Method not found: {fqn}");
                svg = await GraphvizRenderer.RenderSvgAsync(graph);
                _cache.Set(cacheKey, svg, SvgCacheLifetime);
            }
            return Content(svg!, "image/svg+xml");
        }

        [HttpGet]
        public async Task<IActionResult> GraphJson(string fqn)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return BadRequest("fqn required");
            var graph = await BuildGraphAsync(fqn);
            if (graph == null) return NotFound($"Method not found: {fqn}");
            return Json(graph);
        }

        private async Task<CallGraph?> BuildGraphAsync(string fqn)
        {
            var rootSymbol = await _enumerator.ResolveAnyMethodAsync(fqn);
            if (rootSymbol == null) return null;
            var graph = await _builder.BuildAsync(rootSymbol);

            // Compute path-aware boundary call counts so the JSON / UI / any
            // future export consumer all see the same numbers without having
            // to reimplement the polynomial DP.
            graph.BoundaryCallCounts = BoundaryCallCountAnalyzer.Compute(graph);
            return graph;
        }
    }
}
