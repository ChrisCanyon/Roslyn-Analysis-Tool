using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer
{
    public class ErrorReportRunner(
        DependencyGraph dependencyGraph,
        ManualResolutionParser manualResolutionParser)
    {
        public struct DependencyMismatch
        {
            public string Project;
            public string DependantClass;
            public string ErrorMessage;
        }

        public string FindLifetimeMismatches()
        {
            var issues = new List<DependencyMismatch>();
            foreach (var node in dependencyGraph.Nodes)
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
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                sb.AppendLine($"Cycles for {className} dependency tree in project {project}:", ConsoleColor.Cyan);

                var currentPath = new Stack<INamedTypeSymbol>();
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if(classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    SearchForCycle(classNode, project, currentPath, visited, sb);
                }
            }
            else
            {
                sb.AppendLine($"Cycles for dependency in project {project}:", ConsoleColor.Cyan);
                
                foreach (var node in dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project)))
                {
                    var currentPath = new Stack<INamedTypeSymbol>();
                    SearchForCycle(node, project, currentPath, visited, sb);
                }
            }

            return sb;
        }

        private void SearchForCycle(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
        {
            var comparer = new FullyQualifiedNameComparer();
            if (path.Contains(node.Class, comparer))
            {
                sb.Append($"Cycle Detected: ", ConsoleColor.White);
                sb.AppendLine($"{node.Class.Name} <-", ConsoleColor.Red);
                foreach (var step in path)
                {
                    if(comparer.Equals(node.Class, step))
                    {
                        sb.Append($"{step.Name}", ConsoleColor.Red);
                        return;
                    }
                    else
                    {
                        sb.Append($"{step.Name} <- ", ConsoleColor.White);
                    }
                }
                return;
            }
            if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

            visitedNodes.Add(node);
            path.Push(node.Class);

            foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
            {
                SearchForCycle(dependency, project, path, visitedNodes, sb);
            }

            path.Pop();
        }

        public ColoredStringBuilder GenerateExcessiveDependencies(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                sb.AppendLine($"Nodes with excessive dependencies for {className} depedency tree in project {project}:", ConsoleColor.Cyan);

                var currentPath = new Stack<INamedTypeSymbol>();
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if (classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    SearchForExcessiveDependencies(classNode, project, currentPath, visited, sb);
                }
            }
            else
            {
                sb.AppendLine($"Nodes with excessive dependencies in project {project}:", ConsoleColor.Cyan);
                foreach (var node in dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project)))
                {
                    var currentPath = new Stack<INamedTypeSymbol>();
                    SearchForExcessiveDependencies(node, project, currentPath, visited, sb);
                }
            }

            return sb;
        }

        public void SearchForExcessiveDependencies(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
        {
            var comparer = new FullyQualifiedNameComparer();
            if (path.Contains(node.Class, comparer)) return;
            if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

            visitedNodes.Add(node);
            path.Push(node.Class);

            var dependencies = node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project));
            //Dont count IClassA and ClassA as 2 dependencies
            var implmentationsOnly = dependencies.Where(x => !x.IsInterface);
            if(implmentationsOnly.Count() > 5)//TODO determine threshold
            {
                var mainText = $"[WARN] {node.ClassName} has {dependencies.Count()}";
                if (dependencies.Any(dep => dep.RegistrationInfo[project].UnresolvableImplementation))
                {
                    sb.Append(mainText, ConsoleColor.Yellow);
                    sb.AppendLine(" | Unresolveable Implementation - This number may be exaggerated", ConsoleColor.White);
                }
                else
                {
                    sb.AppendLine(mainText, ConsoleColor.Yellow);
                }

            }
            foreach (var dependency in dependencies)
            {
                SearchForExcessiveDependencies(dependency, project, path, visitedNodes, sb);
            }

            path.Pop();
        }

        public ColoredStringBuilder GenerateManualLifecycleManagementReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                sb.AppendLine($"Manually resolved dependencies in {className} dependency tree in project {project}:", ConsoleColor.Cyan);

                var currentPath = new Stack<INamedTypeSymbol>();
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if (classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    SearchForManualLifecycle(classNode, project, currentPath, visited, sb);
                }
            }
            else
            {
                sb.AppendLine($"Manually resolved dependencies in project {project}:", ConsoleColor.Cyan);
                foreach (var node in dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project)))
                {
                    var currentPath = new Stack<INamedTypeSymbol>();
                    SearchForManualLifecycle(node, project, currentPath, visited, sb);
                }
            }

            return sb;
        }

        public void SearchForManualLifecycle(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
        {
            var comparer = new FullyQualifiedNameComparer();
            if (path.Contains(node.Class, comparer)) return;
            if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

            visitedNodes.Add(node);
            path.Push(node.Class);

            List<ManualResolutionInfo> allResolvedSymbols = manualResolutionParser.ManuallyResolvedSymbols;
            foreach(var resolvedSymbol in allResolvedSymbols.Where(x => x.Project == project && 
                                            comparer.Equals(node.Class, x.ResolvedType)))
            {
                sb.AppendLine($"{node.ClassName} manual resolutions:", ConsoleColor.Yellow);
                sb.AppendLine($"\tIn {resolvedSymbol.File} {node.ClassName}", ConsoleColor.White);
                sb.AppendLine($"\t\t{resolvedSymbol.CodeSnippet}", ConsoleColor.Gray);
            }

            List<ManualResolutionInfo> allDisposedSymbols = manualResolutionParser.ManuallyDisposedSymbols;
            foreach (var disposedSymbol in allDisposedSymbols.Where(x => x.Project == project &&
                                                        comparer.Equals(node.Class, x.ResolvedType)))
            {
                sb.AppendLine($"{node.ClassName} manual resolutions:", ConsoleColor.Yellow);
                sb.AppendLine($"\tIn {disposedSymbol.File} {node.ClassName}", ConsoleColor.White);
                sb.AppendLine($"\t\t{disposedSymbol.CodeSnippet}", ConsoleColor.Gray);
            }

            foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
            {
                SearchForManualLifecycle(node, project, path, visitedNodes, sb);
            }
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
