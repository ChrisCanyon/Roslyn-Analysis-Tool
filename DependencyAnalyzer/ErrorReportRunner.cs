using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer
{
    public class ErrorReportRunner(Dictionary<INamedTypeSymbol, DependencyNode> graph)
    {
        Dictionary<INamedTypeSymbol, DependencyNode> DependencyGraph = graph;

        public struct DependencyMismatch
        {
            public string Project;
            public string DependantClass;
            public string ErrorMessage;
        }

        public string FindLifetimeMismatches()
        {
            var issues = new List<DependencyMismatch>();
            foreach (var node in DependencyGraph.Values)
            {
                foreach (var (project, registration) in node.RegistrationInfo)
                {
                    foreach (var dependantReference in node.DependedOnBy)
                    {
                        if (!dependantReference.RegistrationInfo.TryGetValue(project, out var dependantRegistration)) continue;

                        if (dependantRegistration.RegistrationType > registration.RegistrationType)
                        {
                            var errorMessage = ($"\t[{dependantRegistration.RegistrationType}] {dependantReference.ClassName} -> [{registration.RegistrationType}] {node.ClassName}\n");
                            errorMessage += ($"\t\tClass: {dependantReference.ClassName} has lifetime of {dependantRegistration.RegistrationType}\n");
                            errorMessage += ($"\t\tbut references shorter lived class: {node.ClassName} with lifetime {registration.RegistrationType}");
                            issues.Add(new DependencyMismatch()
                            {
                                Project = project,
                                DependantClass = dependantReference.ClassName,
                                ErrorMessage = errorMessage
                            });
                        }
                    }
                }
            }

            var sb = new StringBuilder();
            var projectIssueGroups = issues.OrderBy(x => x.DependantClass).GroupBy(x => x.Project);

            foreach(var projectIssues in projectIssueGroups)
            {
                var project = projectIssues.Key;
                sb.AppendLine($"Issues found in project {project}");
                foreach(var issue in projectIssues)
                {
                    sb.AppendLine(issue.ErrorMessage);
                }
            }

            return sb.ToString();
        }
    }
}
