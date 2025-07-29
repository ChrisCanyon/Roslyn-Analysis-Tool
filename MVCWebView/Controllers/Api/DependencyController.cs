using DependencyAnalyzer;
using DependencyAnalyzer.Visualizers;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace MVCWebView.Controllers.Api
{
    [Route("api/[controller]")]
    [ApiController]
    public class DependencyController : ControllerBase
    {
        DependencyGraph _graph;

        public DependencyController(
            DependencyGraph graph)
        {
            _graph = graph;
        }

        [HttpGet(nameof(GetDependencyTreeText))]
        public IActionResult GetDependencyTreeText(string className, string project)
        {
            var node = _graph.Nodes.Where(x =>
                        string.Compare(x.ClassName, className, true) == 0).FirstOrDefault();
            if (node == null)
            {
                return NotFound($"Class with name {className} not found");
            }
            if (!node.RegistrationInfo.TryGetValue(project, out var projectReg))
            {
                return NotFound($"{className} not registered in {project}");
            }

            var dependency = NodePrinter.PrintDependencyTreeForProject(node, projectReg.ProjectName);
            var consumer = NodePrinter.PrintConsumerTreeForProject(node, projectReg.ProjectName);

            return Content(dependency.ToHTMLString() + "\n\n" + consumer.ToHTMLString(), "text/html");
        }

        [HttpGet("GetSvg")]
        public IActionResult GetSvg(string className, string project)
        {
            var node = _graph.Nodes.Where(x =>
                        string.Compare(x.ClassName, className, true) == 0).FirstOrDefault();
            if (node == null)
            {
                return NotFound($"Class with name {className} not found");
            }
            if (!node.RegistrationInfo.TryGetValue(project, out var projectReg))
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
