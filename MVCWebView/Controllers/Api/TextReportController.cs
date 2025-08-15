using DependencyAnalyzer;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Visualizers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;

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
            [FromQuery] string implementationName = "",
            [FromQuery] string interfaceName = "")
        {
            if (string.IsNullOrWhiteSpace(type))
                return BadRequest("Report type is required.");

            if (string.IsNullOrWhiteSpace(project))
                return BadRequest("Project is required.");

            DependencyNode? node = null;
            if (!entireProject && !allControllers)
            {
                if(implementationName == string.Empty)
                {
                    return BadRequest("Class name is required for single node reports");
                }

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
            }

            ColoredStringBuilder? result = type switch
            {
                "Tree" => _runner.GenerateTreeReport(node, project, entireProject, allControllers),
                //"Cycles" => _runner.GenerateCycleReport(className, project, entireProject, allControllers),
                //"ExcessiveDependencies" => _runner.GenerateExcessiveDependencies(className, project, entireProject, allControllers),
                //"ManualLifecycleManagement" => _runner.GenerateManualLifecycleManagementReport(className, project, entireProject, allControllers),
                //"UnusedMethods" => _runner.GenerateUnusedMethodsReport(className, project, entireProject, allControllers),
                //"ManualInstantiation" => _runner.GenerateManualInstantiationReport(className, project, entireProject, allControllers),
                _ => null
            };

            if (result == null)
                return BadRequest($"Unknown report type: {type}");

            return Content(result.ToHTMLString(), "text/plain");
        }
    }
}
