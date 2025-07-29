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
            [FromQuery] string className,
            [FromQuery] string project,
            [FromQuery] bool entireProject,
            [FromQuery] bool allControllers)
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
            }

            var reportRunner = new ErrorReportRunner(_graph);

            string result = type switch
            {
                "DependencyTree" => await analyzer.GenerateDependencyTreeAsync(className, project),
                "ConsumerTree" => await analyzer.GenerateConsumerTreeAsync(className, project),
                "Cycles" => await analyzer.GenerateCycleReportAsync(className, project, entireProject, allControllers),
                "TooManyDependencies" => await analyzer.GenerateTooManyDependenciesAsync(project, entireProject, allControllers),
                "ManualGetService" => await analyzer.GenerateManualGetServiceReportAsync(project, entireProject, allControllers),
                "ManualDispose" => await analyzer.GenerateManualDisposeReportAsync(project, entireProject, allControllers),
                "UnusedMethods" => await analyzer.GenerateUnusedMethodsReportAsync(project, entireProject, allControllers),
                "NewInsteadOfInjected" => await analyzer.GenerateNewInsteadOfInjectedReportAsync(project, entireProject, allControllers),
                _ => null
            };

            if (result == null)
                return BadRequest($"Unknown report type: {type}");

            return Content(result, "text/plain");
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
