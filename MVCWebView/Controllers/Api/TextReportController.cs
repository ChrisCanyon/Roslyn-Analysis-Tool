using DependencyAnalyzer;
using DependencyAnalyzer.Visualizers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MVCWebView.Controllers.Api
{
    [Route("api/[controller]")]
    [ApiController]
    public class TextReportController : ControllerBase
    {
        DependencyGraph _graph;
        SolutionAnalyzer _solutionAnalyzer;
        DependencyAnalyzer.DependencyAnalyzer _dependencyAnalyzer;

        public TextReportController(
            DependencyGraph graph,
            SolutionAnalyzer solutionAnalyzer,
            DependencyAnalyzer.DependencyAnalyzer dependencyAnalyzer
            )
        {
            _graph = graph;
        }

        [HttpGet(nameof(GetTextReport))]
        public async Task<IActionResult> GetTextReport(
            [FromQuery] string type,
            [FromQuery] string project,
            [FromQuery] bool entireProject,
            [FromQuery] bool allControllers,
            [FromQuery] string className = "")
        {
            if (string.IsNullOrWhiteSpace(type))
                return BadRequest("Report type is required.");

            if (string.IsNullOrWhiteSpace(project))
                return BadRequest("Project is required.");

            if (!entireProject && !allControllers)
            {
                if(className == string.Empty)
                {
                    return BadRequest("Class name is required for single node reports");
                }
                className = _graph.Nodes.Where(x => string.Equals(x.ClassName, className, StringComparison.OrdinalIgnoreCase)).First().ClassName;
            }

            var reportRunner = new ErrorReportRunner(_graph);

            ColoredStringBuilder result = type switch
            {
                "Cycles" => reportRunner.GenerateCycleReport(className, project, entireProject, allControllers),
                "ExcessiveDependencies" => reportRunner.GenerateExcessiveDependencies(className, project, entireProject, allControllers),
                "ManualLifecycleManagement" => reportRunner.GenerateManualLifecycleManagementReport(className, project, entireProject, allControllers),
                "UnusedMethods" => reportRunner.GenerateUnusedMethodsReport(className, project, entireProject, allControllers),
                "NewInsteadOfInjected" => reportRunner.GenerateNewInsteadOfInjectedReport(className, project, entireProject, allControllers),
                _ => null
            };

            if (result == null)
                return BadRequest($"Unknown report type: {type}");

            return Content(result.ToHTMLString(), "text/plain");
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

            return Content(dependency.ToHTMLString(), "text/html");
        }

        [HttpGet(nameof(GetConsumerTreeText))]
        public IActionResult GetConsumerTreeText(string className, string project)
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

            var consumer = NodePrinter.PrintConsumerTreeForProject(node, projectReg.ProjectName);

            return Content(consumer.ToHTMLString(), "text/html");
        }
    }
}
