using DependencyAnalyzer;
using Microsoft.AspNetCore.Mvc;

namespace MVCWebView.Controllers
{
    public class HomeController : Controller
    {
        DependencyGraph _graph;

        public HomeController( 
            DependencyGraph graph)
        {
            _graph = graph;
        }

        public IActionResult Index()
        {
            var allProjects = _graph.Nodes.SelectMany(x => x.RegistrationInfo)
                                            .Select(x => x.Key).Distinct().ToList();

            ViewData["ProjectList"] = allProjects;

            return View();
        }
    }
}
