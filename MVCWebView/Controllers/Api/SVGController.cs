using DependencyAnalyzer.Models;
using DependencyAnalyzer.Visualizers;
using Microsoft.AspNetCore.Mvc;

namespace MVCWebView.Controllers.Api
{
    [Route("api/[controller]")]
    [ApiController]
    public class SVGController : ControllerBase
    {
        DependencyGraph _graph;

        public SVGController(
            DependencyGraph graph)
        {
            _graph = graph;
        }

        [HttpGet("GetSvg")]
        public IActionResult GetSvg(string className, string project)
        {
            var classNodes = _graph.Nodes.Where(x =>
                        string.Compare(x.ClassName, className, true) == 0);
            if (classNodes.Count() == 0)
            {
                return NotFound($"Class with name {className} not found");
            }

            var node = classNodes.Where(x => x.ProjectName == project).FirstOrDefault();
            if (node == null)
            {
                return NotFound($"{className} not registered in {project}");
            }

            var svgPath = GraphvizConverter.CreateGraphvizForProjectNode(node, project, true);

            var svgContext = System.IO.File.ReadAllText(svgPath);
            return Content(svgContext, "image/svg+xml");
        }

        [HttpGet("GetAllControllersSVG")]
        public IActionResult GetAllControllersSVG(string project)
        {
            var svgPath = GraphvizConverter.CreateControllerGraphvizForProject(_graph, project, true);

            var svgContext = System.IO.File.ReadAllText(svgPath);
            return Content(svgContext, "image/svg+xml");
        }

        [HttpGet("GetEntireProjectSVG")]
        public IActionResult GetEntireProjectSVG(string project)
        {
            var svgPath = GraphvizConverter.CreateFullGraphvizForProject(_graph, project, true);

            var svgContext = System.IO.File.ReadAllText(svgPath);
            return Content(svgContext, "image/svg+xml");
        }
    }
}