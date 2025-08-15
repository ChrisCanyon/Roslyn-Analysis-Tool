using DependencyAnalyzer.Models;
using DependencyAnalyzer.Parsers;
using DependencyAnalyzer.Visualizers;
using Microsoft.CodeAnalysis;
using System.Xml.Linq;


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

        /*
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
                    if (path.Contains(node.ImplementationType, comparer))
                    {
                        sb.Append($"Cycle Detected: ", ConsoleColor.White);
                        sb.Append($"{node.ImplementationType.Name} <- ", ConsoleColor.Red);
                        foreach (var step in path)
                        {
                            if(comparer.Equals(node.ImplementationType, step))
                            {
                                sb.Append($"{step.Name}", ConsoleColor.Red);
                                return;
                            }
                            else
                            {
                                sb.Append($"{step.Name} <- ", ConsoleColor.White);
                            }
                        }
                        sb.AppendLine("", ConsoleColor.Black);
                        return;
                    }
                    if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

                    visitedNodes.Add(node);
                    path.Push(node.ImplementationType);

                    foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
                    {
                        SearchForCycle(dependency, project, path, visitedNodes, sb);
                    }

                    path.Pop();
                }
        */

        /*
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
                    if (path.Contains(node.ImplementationType, comparer)) return;
                    if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

                    visitedNodes.Add(node);
                    path.Push(node.ImplementationType);

                    var dependencies = node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project));

                    if(dependencies.Count() > 5)//TODO determine threshold
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
        */

        /*
                public ColoredStringBuilder GenerateManualLifecycleManagementReport(string className, string project, bool entireProject, bool allControllers)
                {
                    var resolveNotes = new ColoredStringBuilder();
                    var disposeNotes = new ColoredStringBuilder();
                    var visited = new List<DependencyNode>();

                    if (!entireProject && !allControllers)
                    {
                        resolveNotes.AppendLine($"Manually resolved dependencies in {className} dependency tree in project {project}:", ConsoleColor.Cyan);
                        disposeNotes.AppendLine($"Manually disposed/released dependencies in {className} dependency tree in project {project}:", ConsoleColor.Cyan);

                        var currentPath = new Stack<INamedTypeSymbol>();
                        var classNode = dependencyGraph.Nodes.Where(x => x.ClassName == className && x.RegistrationInfo.ContainsKey(project)).FirstOrDefault();
                        if (classNode == null)
                        {
                            return resolveNotes.AppendLine($"{className} not registered in {project}", ConsoleColor.DarkMagenta);
                        }
                        SearchForManualLifecycle(classNode, project, currentPath, visited, resolveNotes, disposeNotes);
                    }
                    else
                    {
                        resolveNotes.AppendLine($"Manually resolved dependencies in project {project}:", ConsoleColor.Cyan);
                        disposeNotes.AppendLine($"Manually disposed/released dependencies in project {project}:", ConsoleColor.Cyan);

                        var relevantNodes = dependencyGraph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));

                        if (allControllers)
                        {
                            relevantNodes = relevantNodes.Where(x => x.RegistrationInfo[project].Lifetime == LifetimeTypes.Controller);
                        }

                        foreach (var node in relevantNodes)
                        {
                            var currentPath = new Stack<INamedTypeSymbol>();
                            SearchForManualLifecycle(node, project, currentPath, visited, resolveNotes, disposeNotes);
                        }
                    }

                    return resolveNotes.Append(disposeNotes);
                }

                private void SearchForManualLifecycle(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder resolveNotes, ColoredStringBuilder disposeNotes)
                {
                    var comparer = new FullyQualifiedNameComparer();
                    if (path.Contains(node.ImplementationType, comparer)) return;
                    if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

                    visitedNodes.Add(node);
                    path.Push(node.ImplementationType);

                    var nodeRegistration = node.RegistrationInfo[project];

                    List<ManualLifetimeInteractionInfo> allResolvedSymbols = manualResolutionParser.ManuallyResolvedSymbols;
                    foreach(var resolvedSymbol in allResolvedSymbols.Where(x => x.Project == project && 
                                                    comparer.Equals(node.ImplementationType, x.Type)))
                    {
                        resolveNotes.AppendLine($"{node.ClassName} manual resolutions", ConsoleColor.Yellow);
                        resolveNotes.AppendLine($"\t{resolvedSymbol.InvocationPath}", ConsoleColor.White);
                    }

                    var disposals = manualResolutionParser.ManuallyDisposedSymbols
                        .Where(x => comparer.Equals(node.ImplementationType, x.Type) && project == x.Project).ToList();
                    foreach (var disposedSymbol in disposals)
                    {
                        disposeNotes.AppendLine($"[{nodeRegistration.Lifetime}]{node.ClassName} manual Disposal/Release:", ConsoleColor.Yellow);
                        disposeNotes.AppendLine($"\t{disposedSymbol.CodeSnippet}", ConsoleColor.Yellow);
                        if (nodeRegistration.Lifetime < LifetimeTypes.Singleton)
                        {
                            //Most releases are called on interface but interfaces dont have dependencies. We have to find the implementations for this project
                            if(node.IsInterface)
                            {
                                var implementations = node.ImplementedBy.Where(x => x.RegistrationInfo.ContainsKey(project));
                                foreach(var implmentation in implementations)
                                {
                                    if (FindSensitiveNodesInDependencyTree(implmentation, project, new Stack<INamedTypeSymbol>(), new List<DependencyNode>(), disposeNotes))
                                    {
                                        disposeNotes.AppendLine($"\t{disposedSymbol.InvocationPath}", ConsoleColor.Red);
                                    };
                                }
                            }
                            else
                            {
                                if (FindSensitiveNodesInDependencyTree(node, project, new Stack<INamedTypeSymbol>(), new List<DependencyNode>(), disposeNotes))
                                {
                                    disposeNotes.AppendLine($"\t{disposedSymbol.InvocationPath}", ConsoleColor.Red);
                                };
                            }
                        }
                    }

                    foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
                    {
                        SearchForManualLifecycle(node, project, path, visitedNodes, resolveNotes, disposeNotes);
                    }

                    path.Pop();
                }

                private bool FindSensitiveNodesInDependencyTree(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
                {
                    var comparer = new FullyQualifiedNameComparer();
                    if (path.Contains(node.ImplementationType, comparer)) return false;
                    if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return false;
                    if (node.RegistrationInfo[project].Lifetime == LifetimeTypes.Singleton) return false; //doesnt actually release

                    visitedNodes.Add(node);
                    path.Push(node.ImplementationType);

                    bool ret = false;

                    var regi = node.RegistrationInfo[project];

                    if (regi.Lifetime == LifetimeTypes.PerWebRequest)
                    {
                        sb.AppendLine($"\t[{regi.Lifetime}]{node.ClassName} released downstream", ConsoleColor.Red);
                        ret = true;
                    }

                    foreach (var dependency in node.DependsOn.Where(x => x.RegistrationInfo.ContainsKey(project)))
                    {
                        ret = ret || FindSensitiveNodesInDependencyTree(node, project, path, visitedNodes, sb);
                    }

                    path.Pop();
                    return ret;
                }
        */

        /*
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

        */


        /*
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

        */

        public ColoredStringBuilder GenerateTreeReport(DependencyNode? startNode, string project, bool entireProject, bool allControllers)
        {
            var sb = new ColoredStringBuilder();
            var visited = new List<DependencyNode>();

            if (!entireProject && !allControllers)
            {
                //todo remove node printer
                sb.AppendLine($"Dependency Tree from node {startNode.ClassName}", ConsoleColor.Cyan);
                sb.Append(NodePrinter.PrintDependencyTreeForProject(startNode, project));
                sb.AppendLine($"Consumer Tree from nose {startNode.ClassName}", ConsoleColor.Cyan);
                sb.Append(NodePrinter.PrintConsumerTreeForProject(startNode, project));
            }
            else
            {
                sb.AppendLine($"Lifetime Violations for Graph", ConsoleColor.Cyan);
                var relevantNodes = dependencyGraph.Nodes.Where(x => x.ProjectName == project);

                if (allControllers)
                {
                    relevantNodes = relevantNodes.Where(x => x.Lifetime == LifetimeTypes.Controller);
                }

                SearchForLifetimeViolations(relevantNodes, sb);
            }

            return sb;
        }

        public void SearchForLifetimeViolations(IEnumerable<DependencyNode> searchNodes, ColoredStringBuilder sb)
        {
            var issues = new List<DependencyMismatch>();
            foreach (var currentNode in searchNodes)
            {
                foreach (var dependantReference in currentNode.DependedOnBy)
                {
                    if (dependantReference.Lifetime > currentNode.Lifetime)
                    {
                        sb.AppendLine($"[{dependantReference.Lifetime}] {dependantReference.ClassName} -> [{currentNode.Lifetime}] {currentNode.ClassName}\n", ConsoleColor.Red);
                        sb.AppendLine($"\tClass: {dependantReference.ClassName} has lifetime of {dependantReference.Lifetime}\n", ConsoleColor.Gray);
                        sb.AppendLine($"\tbut references shorter lived class: {currentNode.ClassName} with lifetime {currentNode.Lifetime}", ConsoleColor.Gray);
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
        private void TODO(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, ColoredStringBuilder sb)
        {
            //TODO stop referencing this
        }
    }
}
