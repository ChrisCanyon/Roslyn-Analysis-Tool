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
        readonly DependencyGraph _graph;
        readonly ErrorReportRunner _runner;
        public TextReportController(
            DependencyGraph graph,
            ErrorReportRunner runner
            )
        {
            _graph = graph;
            _runner = runner;
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


            ColoredStringBuilder? result = type switch
            {
                "Tree" => _runner.GenerateTreeReport(className, project, entireProject, allControllers),
                "Cycles" => _runner.GenerateCycleReport(className, project, entireProject, allControllers),
                "ExcessiveDependencies" => _runner.GenerateExcessiveDependencies(className, project, entireProject, allControllers),
                "ManualLifecycleManagement" => _runner.GenerateManualLifecycleManagementReport(className, project, entireProject, allControllers),
                "UnusedMethods" => _runner.GenerateUnusedMethodsReport(className, project, entireProject, allControllers),
                "ManualInstantiation" => _runner.GenerateManualInstantiationReport(className, project, entireProject, allControllers),
                _ => null
            };

            if (result == null)
                return BadRequest($"Unknown report type: {type}");

            return Content(result.ToHTMLString(), "text/plain");
        }
    }
}
