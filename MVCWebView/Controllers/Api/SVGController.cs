using DependencyAnalyzer.Models;
using DependencyAnalyzer.Visualizers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Build.Framework;
using Microsoft.Extensions.DependencyModel;

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
        public IActionResult GetSvg(string implementationName, string project, string interfaceName = "")
        {
            IEnumerable<DependencyNode> classNodes = _graph.Nodes.Where(x =>
                        string.Compare(x.ClassName, implementationName, true) == 0);
            if (classNodes.Count() == 0)
            {
                return NotFound($"Class with name {implementationName} not found");
            }

            IEnumerable<DependencyNode> currentProjectNodes = classNodes.Where(x => x.ProjectName == project);
            if (currentProjectNodes.Count() == 0)
            {
                return NotFound($"{implementationName} not registered in {project}");
            }

            DependencyNode? node;
            if (interfaceName == string.Empty)
            {
                //Pure concrete impl
                node = currentProjectNodes.First();
            }
            else
            {
                node = currentProjectNodes.Where(x => x.ServiceInterface != null &&
                            string.Compare(x.ServiceInterface.ToDisplayString(), interfaceName, true) == 0)
                        .FirstOrDefault();

                if (node == null)
                {
                    return NotFound($"No registration for {implementationName} implementing {interfaceName} in {project}");
                }
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