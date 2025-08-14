using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Text;
/*
namespace DependencyAnalyzer.Visualizers
{
    public static class GraphvizConverter
    {
        public static string CreateControllerGraphvizForProject(DependencyGraph graph, string project, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{project}-Controllers.dot");
            var svgPath = Path.Combine(basePath, $"{project}-Controllers.svg");

            File.WriteAllText(dotPath, GetGraphvizStringForController(graph, project));

            GenerateSvg(dotPath, svgPath);

            return svgPath;
        }


        public static string CreateFullGraphvizForProject(DependencyGraph graph, string project, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{project}-Full.dot");
            var svgPath = Path.Combine(basePath, $"{project}-Full.svg");

            File.WriteAllText(dotPath, GetGraphvizStringForEntireProject(graph, project));

            GenerateSvg(dotPath, svgPath);

            return svgPath;
        }

        public static string CreateConsumerGraphvizForProject(DependencyNode startNode, string project, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Consumer.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Consumer.svg");

            File.WriteAllText(dotPath, GetConsumerGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);

            return svgPath;
        }

        public static string CreateDependencyGraphvizForProject(DependencyNode startNode, string project, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Dependency.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Dependency.svg");

            File.WriteAllText(dotPath, GetDependencyGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);

            return svgPath;
        }

        public static string CreateGraphvizForProjectNode(DependencyNode startNode, string project, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Full.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Full.svg");

            File.WriteAllText(dotPath, GetNodeGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);

            return svgPath;
        }
        private static string getBasePath(bool forWeb)
        {
            var basePath = "";
            if (forWeb)
            {
                basePath = Directory.GetParent(Directory.GetCurrentDirectory()).FullName;
            }
            else
            {
                basePath = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
            }

            return Path.Combine(basePath, "output");
        }

        private static void GenerateSvg(string dotFilePath, string outputSvgPath)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dot",
                    Arguments = $"-Tsvg \"{dotFilePath}\" -o \"{outputSvgPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new Exception($"Graphviz error: {error}");
            }
        }



        private static string GetConsumerGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=RL;");
            CreateGraphvizLegend(sb);
            var currentPath = new Stack<INamedTypeSymbol>();
            var visited = new List<DependencyNode>();
            TraverseConsumerGraph(startNode, project, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void TraverseConsumerGraph(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, StringBuilder sb)
        {
            if (!node.RegistrationInfo.TryGetValue(project, out var currentNodeLifetime)) return;
            if (path.Contains(node.ImplementationType, new FullyQualifiedNameComparer()))
            {
                Console.WriteLine($"Cycle detected:\n\t" +
                    $"{String.Join(" ->", path.Select(x => x.Name))} -> {node.ImplementationType.Name}");
                return;
            }
            if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

            visitedNodes.Add(node);
            path.Push(node.ImplementationType);

            CreateNode(sb, node.ClassName, currentNodeLifetime.Lifetime);

            foreach (var dependant in node.DependedOnBy)
            {
                TraverseConsumerGraph(dependant, project, path, visitedNodes, sb);
            }

            foreach (var dependant in node.DependedOnBy)
            {
                if (!dependant.RegistrationInfo.TryGetValue(project, out var dependantReg)) continue;
                string label = "";
                if (dependantReg.Lifetime > currentNodeLifetime.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[label=\"Valid\", color=green, fontcolor=green]";
                }

                CreateEdge(sb, dependant.ClassName, node.ClassName, label);
            }

            path.Pop();
        }

        private static string GetNodeGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=LR;");
            CreateGraphvizLegend(sb);
            var currentPath = new Stack<INamedTypeSymbol>();
            var visited = new List<DependencyNode>();
            TraverseConsumerGraph(startNode, project, currentPath, visited, sb);
            visited = new List<DependencyNode>();
            TraverseDependencyGraph(startNode, project, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetGraphvizStringForEntireProject(DependencyGraph graph, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=LR;");
            CreateGraphvizLegend(sb);
            var projectNodes = graph.Nodes.Where(x => x.RegistrationInfo.ContainsKey(project));
            foreach (var node in projectNodes)
            {
                ProcessSingleNode(node, project, sb);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        //this is called for every node in a project
        private static void ProcessSingleNode(DependencyNode node, string project, StringBuilder sb)
        {
            var dependencies = GetReleventInterfaceAndImplementations(node.DependsOn, project);
            var registration = node.RegistrationInfo[project];

            //Create node for self
            CreateNode(sb, node.ClassName, registration.Lifetime);

            foreach (var dependency in dependencies)
            {
                string label;
                if (!dependency.RegistrationInfo.TryGetValue(project, out var dependencyReg))
                {
                    //Dependency was not registered for project. this is a runtime error probably
                    if (registration.IsFactoryResolved)
                    {
                        var color = "\"#808080\"";
                        CreateNode(sb, dependency.ClassName, LifetimeTypes.Unregistered);
                        label = $"[label=\"WARNING NOT REGISTERED\\nFactory\", color={color}, fontcolor={color}]";
                        CreateEdge(sb, node.ClassName, dependency.ClassName, label);
                    }
                    else
                    {
                        var color = GetBackgroundColorForLifetime(LifetimeTypes.Unregistered);
                        CreateNode(sb, dependency.ClassName, LifetimeTypes.Unregistered);
                        label = $"[label=\"WARNING NOT REGISTERED\", color={color}, fontcolor={color}]";
                        CreateEdge(sb, node.ClassName, dependency.ClassName, label);
                    }
                    continue;
                }

                if (dependencyReg.Lifetime < registration.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[color=green, fontcolor=green]";
                }

                //only create edge. the node will be created in subsequent calls to this method
                CreateEdge(sb, node.ClassName, dependency.ClassName, label);
            }
        }

        private static string GetDependencyGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=LR;");
            CreateGraphvizLegend(sb);
            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].Lifetime;
            var visited = new List<DependencyNode>();
            TraverseDependencyGraph(startNode, project, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }


        private static string GetGraphvizStringForController(DependencyGraph graph, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph controllers {");
            sb.AppendLine("\t rankdir=LR;");
            CreateGraphvizLegend(sb);
            var controllersInProject = graph.Nodes.Where(
                node => node.RegistrationInfo.Any(reg => reg.Key == project && reg.Value.Lifetime == LifetimeTypes.Controller));

            var visited = new List<DependencyNode>();
            foreach (var node in controllersInProject)
            {
                var currentPath = new Stack<INamedTypeSymbol>();
                TraverseDependencyGraph(node, project, currentPath, visited, sb);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void CreateNode(StringBuilder sb, string className, LifetimeTypes lifetime)
        {
            sb.AppendLine($"\"{className}\" " +
                        $"[label=\"{className}\\n({lifetime})\"," +
                        $" color={GetBackgroundColorForLifetime(lifetime)}," +
                        $" fontcolor={GetTextColorForLifetime(lifetime)}," +
                        $" style=filled];");
        }

        private static void CreateEdge(StringBuilder sb, string to, string from, string label)
        {
            sb.AppendLine($"\"{to}\" -> \"{from}\" {label}");
        }

        //TODO MAKE THIS COMMON WITH NODE PRINTER
        private static List<DependencyNode> GetReleventInterfaceAndImplementations(IEnumerable<DependencyNode> dependencies, string project)
        {
            var depInterfaces = dependencies.Where(x => x.IsInterface).ToList();
            var relevantImplementations = dependencies.Where(dependency =>
            {
                //if this is an implementation for a needed interface that is registered in the project
                if (dependency.RegistrationInfo.ContainsKey(project)
                && dependency.Implements.Any(inter => depInterfaces.Contains(inter)))
                {
                    return true;
                }
                //Or if this is a dependency that isnt an interface implementation
                if (dependency.Implements.Count == 0 && dependency.IsInterface == false)
                {
                    return true;
                }
                return false;
            });

            return depInterfaces.Concat(relevantImplementations).ToList();
        }

        private static void TraverseDependencyGraph(DependencyNode node, string project, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, StringBuilder sb, bool ambiguousRegistrationSubDependency = false)
        {
            if (path.Contains(node.ImplementationType, new FullyQualifiedNameComparer()))
            {
                Console.WriteLine($"Cycle detected:\n\t" +
                    $"{String.Join(" -> ", path.Select(x => x.Name))} -> {node.ImplementationType.Name}");
                return;
            }
            if (visitedNodes.Any(x => x.ClassName == node.ClassName)) return;

            visitedNodes.Add(node);
            path.Push(node.ImplementationType);

            if (!node.RegistrationInfo.TryGetValue(project, out var currentNodeRegistration))
            {
                //Create this node and edge from the parent perspective
                return;
            }
            
            CreateNode(sb, node.ClassName, currentNodeRegistration.Lifetime);

            foreach (var dependency in node.DependsOn)
            {
                TraverseDependencyGraph(dependency, project, path, visitedNodes, sb, ambiguousRegistrationSubDependency);
            }

            ambiguousRegistrationSubDependency = ambiguousRegistrationSubDependency || currentNodeRegistration.UnresolvableImplementation;

            var dependencies = GetReleventInterfaceAndImplementations(node.DependsOn, project);
            foreach (var dependency in dependencies)
            {
                string label;
                if (!dependency.RegistrationInfo.TryGetValue(project, out var dependencyReg))
                {
                    // This dependency was not registered for this project.
                    // In most cases, this is likely a runtime resolution failure.
                    // However, if this node is part of a registration with an unresolvable implementation
                    // (e.g., a factory returning an unknown type), this missing registration might be acceptable.
                    if (!ambiguousRegistrationSubDependency)
                    {
                        if (currentNodeRegistration.IsFactoryResolved)
                        {
                            var color = "\"#808080\"";
                            CreateNode(sb, dependency.ClassName, LifetimeTypes.Unregistered);
                            label = $"[label=\"WARNING NOT REGISTERED\\nFactory\", color={color}, fontcolor={color}]";
                            CreateEdge(sb, node.ClassName, dependency.ClassName, label);
                        }
                        else
                        {
                            var color = GetBackgroundColorForLifetime(LifetimeTypes.Unregistered);
                            CreateNode(sb, dependency.ClassName, LifetimeTypes.Unregistered);
                            label = $"[label=\"WARNING NOT REGISTERED\", color={color}, fontcolor={color}]";
                            CreateEdge(sb, node.ClassName, dependency.ClassName, label);
                        }
                    }

                    continue;
                }

                if (dependencyReg.Lifetime < currentNodeRegistration.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[label=\"Valid\", color=green, fontcolor=green]";
                }

                CreateEdge(sb, node.ClassName, dependency.ClassName, label);
            }

            path.Pop();
        }

        private static void CreateGraphvizLegend(StringBuilder sb)
        {
            sb.AppendLine($"Legend [label=<\r\n" +
                $"<TABLE BORDER=\"0\" CELLBORDER=\"1\" CELLSPACING=\"0\" CELLPADDING=\"4\">\r\n" +
                $"  <TR><TD COLSPAN=\"2\"><B>Legend</B></TD></TR>");

            foreach (var lifetime in Enum.GetValues(typeof(LifetimeTypes)).Cast<LifetimeTypes>())
            {
                sb.AppendLine($"  <TR><TD BGCOLOR={GetBackgroundColorForLifetime(lifetime)}></TD><TD>{lifetime}</TD></TR>");
            }

            sb.AppendLine($"</TABLE>\r\n>, shape=plaintext];");
        }

        private static string GetBackgroundColorForLifetime(LifetimeTypes lifetime)
        {
            return lifetime switch
            {
                LifetimeTypes.Transient => "\"#ccffff\"",  // Light Cyan
                LifetimeTypes.PerWebRequest => "\"#fff2cc\"",  // Light Yellow/Orange
                LifetimeTypes.Singleton => "\"#ffcccc\"",  // Light Red,
                LifetimeTypes.Controller => "\"#9b95c9\"", //light purple
                _ => "\"#ff00ff\""   // Default Magenta
            };
        }
        
        private static string GetTextColorForLifetime(LifetimeTypes lifetime)
        {
            return "black";
        }
    }
}
*/