using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Visualizers;
using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer
{
    //TODO combine these somehow to take make only one public method
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
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }
                
                foreach (var node in relevantNodes)
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
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }

                foreach (var node in relevantNodes)
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
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }

                foreach (var node in relevantNodes)
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

            var nodeRegistration = node.RegistrationInfo[project];

            List<ManualLifetimeInteractionInfo> allResolvedSymbols = manualResolutionParser.ManuallyResolvedSymbols;
            foreach(var resolvedSymbol in allResolvedSymbols.Where(x => x.Project == project && 
                                            comparer.Equals(node.Class, x.Type)))
            {
                sb.AppendLine($"{node.ClassName} manual resolutions", ConsoleColor.Yellow);
                sb.AppendLine($"\tIn {resolvedSymbol.File} {node.ClassName}", ConsoleColor.White);
                sb.AppendLine($"\t{resolvedSymbol.CodeSnippet}", ConsoleColor.Gray);
            }

            List<ManualLifetimeInteractionInfo> allDisposedSymbols = manualResolutionParser.ManuallyDisposedSymbols;
            foreach (var disposedSymbol in allDisposedSymbols.Where(x => x.Project == project &&
                                                        comparer.Equals(node.Class, x.Type)))
            {
                if(nodeRegistration.Lifetime > LifetimeTypes.Transient)
                {
                    sb.AppendLine($"~~WARN {nodeRegistration.Lifetime} Potential Early Disposal~~", ConsoleColor.Red);
                }
                sb.AppendLine($"{node.ClassName} manual Disposal/Release:", ConsoleColor.Yellow);
                sb.AppendLine($"\tIn {disposedSymbol.File} {node.ClassName}", ConsoleColor.White);
                sb.AppendLine($"\t{disposedSymbol.CodeSnippet}", ConsoleColor.Gray);
            }

            foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
            {
                SearchForManualLifecycle(node, project, path, visitedNodes, sb);
            }
        }

        private void TODO(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
        {
          //TODO stop referencing this
        }

        public ColoredStringBuilder GenerateUnusedMethodsReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                sb.AppendLine($"TODO UNUSED METHODS", ConsoleColor.Cyan);

                var currentPath = new Stack<INamedTypeSymbol>();
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if (classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    TODO(classNode, project, currentPath, visited, sb);
                }
            }
            else
            {
                sb.AppendLine($"TODO UNUSED METHODS", ConsoleColor.Cyan);
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }

                foreach (var node in relevantNodes)
                {
                    var currentPath = new Stack<INamedTypeSymbol>();
                    TODO(node, project, currentPath, visited, sb);
                }
            }

            return sb;
        }

        public ColoredStringBuilder GenerateManualInstantiationReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                sb.AppendLine($"TODO MANUAL INSTANTIATION", ConsoleColor.Cyan);

                var currentPath = new Stack<INamedTypeSymbol>();
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if (classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    TODO(classNode, project, currentPath, visited, sb);
                }
            }
            else
            {
                sb.AppendLine($"TODO MANUAL INSTANTIATION", ConsoleColor.Cyan);
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }

                foreach (var node in relevantNodes)
                {
                    var currentPath = new Stack<INamedTypeSymbol>();
                    TODO(node, project, currentPath, visited, sb);
                }
            }

            return sb;
        }

       
        public ColoredStringBuilder GenerateTreeReport(string className, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                if (classNode == null)
                {
                    sb.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                }
                else
                {
                    //todo remove node printer
                    sb.AppendLine($"Dependency Tree from node {className}", ConsoleColor.Cyan);
                    sb.Append(NodePrinter.PrintDependencyTreeForProject(classNode, project));
                    sb.AppendLine($"Consumer Tree from nose {className}", ConsoleColor.Cyan);
                    sb.Append(NodePrinter.PrintConsumerTreeForProject(classNode, project));
                }
            }
            else
            {
                sb.AppendLine($"Lifetime Violations for Graph", ConsoleColor.Cyan);
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                }

                SearchForLifetimeViolations(relevantNodes, sb);
            }

            return sb;
        }

        public void SearchForLifetimeViolations(IEnumerable<DependencyNode> searchNodes, ColoredStringBuilder sb)
        {
            var issues = new List<DependencyMismatch>();
            foreach (var node in searchNodes)
            {
                foreach (var (project, registration) in node.RegistrationInfo)
                {
                    foreach (var dependantReference in node.DependedOnBy)
                    {
                        if (!dependantReference.RegistrationInfo.TryGetValue(project, out var dependantRegistration)) continue;

                        if (dependantRegistration.Lifetime > registration.Lifetime)
                        {
                            sb.AppendLine($"[{dependantRegistration.Lifetime}] {dependantReference.ClassName} -> [{registration.Lifetime}] {node.ClassName}\n", ConsoleColor.Red);
                            sb.AppendLine($"\tClass: {dependantReference.ClassName} has lifetime of {dependantRegistration.Lifetime}\n", ConsoleColor.Gray);
                            sb.AppendLine($"\tbut references shorter lived class: {node.ClassName} with lifetime {registration.Lifetime}", ConsoleColor.Gray);
                        }
                    }
                }
            }

            var projectIssueGroups = issues.OrderBy(x => x.DependantClass).GroupBy(x => x.Project);

            foreach (var projectIssues in projectIssueGroups)
            {
                var project = projectIssues.Key;
                sb.AppendLine($"Issues found in project {project}", ConsoleColor.Red);
                foreach (var issue in projectIssues)
                {
                    sb.AppendLine($"\t{issue.ErrorMessage}", ConsoleColor.Gray);
                }
            }
        }
    }
}
