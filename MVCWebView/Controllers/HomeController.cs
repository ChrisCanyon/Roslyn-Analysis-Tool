using DependencyAnalyzer.Models;
using GatewayCallGraph;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;

namespace MVCWebView.Controllers
{
    public class HomeController : Controller
    {
        DependencyGraph _graph;
        ControllerEnumerator _controllerEnumerator;

        public HomeController(
            DependencyGraph graph,
            ControllerEnumerator controllerEnumerator)
        {
            _graph = graph;
            _controllerEnumerator = controllerEnumerator;
        }

        public class ClassSelectItem
        {
            public required string Display { get; set; }
            public required string Value { get; set; }
        }

        public class ClassSelectItemComparer : IEqualityComparer<ClassSelectItem>
        {
            public bool Equals(ClassSelectItem? x, ClassSelectItem? y)
            {
                if (x is null || y is null)
                    return false;

                return GetKey(x) == GetKey(y);
            }

            public int GetHashCode(ClassSelectItem? obj)
            {
                return GetKey(obj).GetHashCode();
            }

            private static string GetKey(ClassSelectItem? selectItem)
            {
                return selectItem == null ? "" : selectItem.Value;
            }
        }

            public IActionResult Index()
        {
            var allProjects = _graph.Nodes.Select(x => x.ProjectName)
                                    .Distinct().ToList();

            ViewData["ProjectList"] = allProjects;

            var allImplementations = _graph.Nodes.Select(x => {
                    if(x.ServiceInterface != null)
                    {
                        return new ClassSelectItem
                        {
                            Display = $"{x.ImplementationType.Name} : {x.ServiceInterface.Name}",
                            Value = $"{x.ImplementationType.ToDisplayString()} : {x.ServiceInterface.ToDisplayString()}"
                        };
                    }
                    else
                    {
                        return new ClassSelectItem
                        {
                            Display = $"{x.ImplementationType.Name}",
                            Value = $"{x.ImplementationType.ToDisplayString()}"
                        };
                    } 
                }).Distinct(new ClassSelectItemComparer()).ToList();

            ViewData["ClassNames"] = allImplementations;

            return View();
        }

        /// <summary>
        /// Renders the gateway-call-graph UI: a per-controller call graph viewer
        /// that walks DOWN from a controller action through its callees until it
        /// reaches a gateway interface method. Click intermediate nodes to re-root
        /// the graph at that node. Distinct from Index() which is the dependency
        /// graph for DI-container analysis.
        /// </summary>
        public async Task<IActionResult> CallGraph()
        {
            var actions = await _controllerEnumerator.ListAsync();

            // Show the full controller type (with namespace) so users can tell apart
            // multiple controllers that share a short class name. The Value stays as
            // the action's full method-display string — that's what the API expects.
            var items = actions.Select(a => new ClassSelectItem
            {
                Display = $"[{a.ProjectName}] {a.ControllerType}.{a.MethodName}",
                Value = a.ActionFqn,
            }).ToList();

            ViewData["ControllerActions"] = items;
            return View();
        }
    }
}
