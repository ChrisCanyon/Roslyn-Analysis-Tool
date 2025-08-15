using DependencyAnalyzer.Comparers;
using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Text;

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

        public static string CreateConsumerGraphvizForProject(DependencyNode startNode, bool forWeb)
        {
            var basePath = getBasePath(forWeb);

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{startNode.ProjectName}-Consumer.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{startNode.ProjectName}-Consumer.svg");

            File.WriteAllText(dotPath, GetConsumerGraphvizString(startNode));

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

        private static string GetConsumerGraphvizString(DependencyNode startNode)
        {
            var sb = new StringBuilder();

            GenerateBoilerPlateHeader(sb, "RL");
            var currentPath = new Stack<INamedTypeSymbol>();
            var visited = new List<DependencyNode>();
            TraverseConsumerGraph(startNode, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void TraverseConsumerGraph(DependencyNode currentNode, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, StringBuilder sb)
        {
            if (path.Contains(currentNode.ImplementationType, new FullyQualifiedNameComparer()))
            {
                Console.WriteLine($"Cycle detected:\n\t" +
                    $"{String.Join(" ->", path.Select(x => x.Name))} -> {currentNode.ImplementationType.Name}");
                return;
            }
            if (visitedNodes.Any(x => x.ClassName == currentNode.ClassName)) return;

            visitedNodes.Add(currentNode);
            path.Push(currentNode.ImplementationType);

            CreateNode(sb, currentNode);

            foreach (var dependant in currentNode.DependedOnBy)
            {
                TraverseConsumerGraph(dependant, path, visitedNodes, sb);
            }

            foreach (var dependant in currentNode.DependedOnBy)
            {
                string label = "";
                if (dependant.Lifetime > currentNode.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[label=\"Valid\", color=green, fontcolor=green]";
                }

                CreateEdge(sb, dependant, currentNode, label);
            }

            path.Pop();
        }

        private static void AddUnsatisfiedDependencies(DependencyNode node, StringBuilder sb)
        {
            foreach (var dependency in node.UnsatisfiedDependencies())
            {
                var color = GetBackgroundColorForLifetime(LifetimeTypes.Unregistered);
                CreateUnregisteredNode(sb, dependency);
                CreateUnregisteredEdge(sb, node, dependency);
            }
        }

        private static void GenerateBoilerPlateHeader(StringBuilder sb, string rankdir = "LR")
        {
            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine($"\t rankdir={rankdir};");
            sb.AppendLine("\t node [shape=ellipse, style=\"filled,dashed\", color=lightgray, fontcolor=black];");
            CreateGraphvizLegend(sb);
        }

        private static string GetNodeGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            GenerateBoilerPlateHeader(sb);
            var currentPath = new Stack<INamedTypeSymbol>();
            var visited = new List<DependencyNode>();
            TraverseConsumerGraph(startNode, currentPath, visited, sb);
            visited = new List<DependencyNode>();
            TraverseDependencyGraph(startNode, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetGraphvizStringForEntireProject(DependencyGraph graph, string project)
        {
            var sb = new StringBuilder();

            GenerateBoilerPlateHeader(sb);
            var projectNodes = graph.Nodes.Where(x => x.ProjectName == project);
            foreach (var node in projectNodes)
            {
                ProcessSingleNode(node, sb);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        //this is called for every node in a project
        private static void ProcessSingleNode(DependencyNode currentMode, StringBuilder sb)
        {
            //Create node for self
            CreateNode(sb, currentMode);

            foreach (var dependency in currentMode.DependsOn)
            {
                string label;

                if (dependency.Lifetime < currentMode.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[color=green, fontcolor=green]";
                }

                //only create edge. the node will be created in subsequent calls to this method
                CreateEdge(sb, currentMode, dependency, label);
            }

            AddUnsatisfiedDependencies(currentMode, sb);
        }
        
        private static string GetDependencyGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            GenerateBoilerPlateHeader(sb);
            var currentPath = new Stack<INamedTypeSymbol>();
            var visited = new List<DependencyNode>();
            TraverseDependencyGraph(startNode, currentPath, visited, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }


        private static string GetGraphvizStringForController(DependencyGraph graph, string project)
        {
            var sb = new StringBuilder();

            GenerateBoilerPlateHeader(sb);
            var controllersInProject = graph.Nodes.Where(
                node => node.ProjectName == project && node.Lifetime == LifetimeTypes.Controller);

            var visited = new List<DependencyNode>();
            foreach (var node in controllersInProject)
            {
                var currentPath = new Stack<INamedTypeSymbol>();
                TraverseDependencyGraph(node, currentPath, visited, sb);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void TraverseDependencyGraph(DependencyNode currentNode, Stack<INamedTypeSymbol> path, List<DependencyNode> visitedNodes, StringBuilder sb)
        {
            if (path.Contains(currentNode.ImplementationType, new FullyQualifiedNameComparer()))
            {
                Console.WriteLine($"Cycle detected:\n\t" +
                    $"{String.Join(" -> ", path.Select(x => x.Name))} -> {currentNode.ImplementationType.Name}");
                return;
            }
            if (visitedNodes.Any(x => x.ClassName == currentNode.ClassName)) return;

            visitedNodes.Add(currentNode);
            path.Push(currentNode.ImplementationType);

            CreateNode(sb, currentNode);

            foreach (var dependency in currentNode.DependsOn)
            {
                TraverseDependencyGraph(dependency, path, visitedNodes, sb);
                string label;

                if (dependency.Lifetime < currentNode.Lifetime)
                {
                    label = "[label=\"❌ Invalid\", color=red, fontcolor=red]";
                }
                else
                {
                    label = "[label=\"Valid\", color=green, fontcolor=green]";
                }

                CreateEdge(sb, currentNode, dependency, label);
            }

            AddUnsatisfiedDependencies(currentNode, sb);

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

        private static void CreateUnregisteredNode(StringBuilder sb, INamedTypeSymbol symbol)
        {
            string nodeId = symbol.ToDisplayString();
            string nodeLabel = symbol.Name;
            LifetimeTypes lifetime = LifetimeTypes.Unregistered;

            sb.AppendLine($"\"{nodeId}\" " +
                        $"[label=\"{nodeLabel}\"," +
                        $" color={GetBackgroundColorForLifetime(lifetime)}," +
                        $" fontcolor={GetTextColorForLifetime(lifetime)}," +
                        $" style=filled];");
        }

        private static void CreateUnregisteredEdge(StringBuilder sb, DependencyNode from, INamedTypeSymbol to)
        {
            var color = GetBackgroundColorForLifetime(LifetimeTypes.Unregistered);
            string label = $"[label=\"WARNING NOT REGISTERED\", color={color}, fontcolor={color}]";

            sb.AppendLine($"\"{GetIdFromNode(from)}\" -> \"{to.ToDisplayString()}\" {label}");
        }


        private static void CreateNode(StringBuilder sb, DependencyNode node)
        {
            string nodeId = GetIdFromNode(node);
            string nodeLabel = node.ClassName;
            LifetimeTypes lifetime = node.Lifetime;

            if (node.ServiceInterface != null)
            {
                nodeLabel = $"{node.ImplementationType.Name} : {node.ServiceInterface.Name}\n{lifetime}";
            }
            else
            {
                nodeLabel = $"{node.ImplementationType.Name}\n{lifetime}";
            }

            sb.AppendLine($"\"{nodeId}\"" +
                        $"[label=\"{nodeLabel}\"," +
                        $" color={GetBackgroundColorForLifetime(lifetime)}," +
                        $" fontcolor={GetTextColorForLifetime(lifetime)}," +
                        $" style=filled];");
        }

        private static string GetIdFromNode(DependencyNode node)
        {
            string nodeId = "";
            if (node.ServiceInterface != null)
            {
                nodeId = $"{node.ImplementationType.ToDisplayString()} : {node.ServiceInterface.ToDisplayString()}";
            }
            else
            {
                nodeId = $"{node.ImplementationType.ToDisplayString()}";
            }
            return nodeId;
        }

        private static void CreateEdge(StringBuilder sb, DependencyNode from, DependencyNode to, string label)
        {
            sb.AppendLine($"\"{GetIdFromNode(from)}\" -> \"{GetIdFromNode(to)}\" {label}");
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
