using System.Text;

namespace DependencyAnalyzer
{
    public class ErrorReportRunner(DependencyGraph graph)
    {
        DependencyGraph DependencyGraph = graph;

        public struct DependencyMismatch
        {
            public string Project;
            public string DependantClass;
            public string ErrorMessage;
        }

        public string FindLifetimeMismatches()
        {
            var issues = new List<DependencyMismatch>();
            foreach (var node in DependencyGraph.Nodes)
            {
                foreach (var (project, registration) in node.RegistrationInfo)
                {
                    foreach (var dependantReference in node.DependedOnBy)
                    {
                        if (!dependantReference.RegistrationInfo.TryGetValue(project, out var dependantRegistration)) continue;

                        if (dependantRegistration.Lifetime > registration.Lifetime)
                        {
                            var errorMessage = ($"\t[{dependantRegistration.Lifetime}] {dependantReference.ClassName} -> [{registration.Lifetime}] {node.ClassName}\n");
                            errorMessage += ($"\t\tClass: {dependantReference.ClassName} has lifetime of {dependantRegistration.Lifetime}\n");
                            errorMessage += ($"\t\tbut references shorter lived class: {node.ClassName} with lifetime {registration.Lifetime}");
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

        public ColoredStringBuilder GenerateCycleReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

        public ColoredStringBuilder GenerateTooManyDependencies(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

        public ColoredStringBuilder GenerateManualGetServiceReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

        public ColoredStringBuilder GenerateManualDisposeReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

        public ColoredStringBuilder GenerateUnusedMethodsReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

        public ColoredStringBuilder GenerateNewInsteadOfInjectedReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();

            //TODO implement

            return sb;
        }

    }
}
