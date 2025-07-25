using Microsoft.CodeAnalysis;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Text;
using System.Xml.Linq;

namespace DependencyAnalyzer.Visualizers
{
    public static class GraphvizConverter
    {
        

        public static void CreateConsumerGraphvizForProject(DependencyNode startNode, string project)
        {
            var basePath = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
            basePath = Path.Combine(basePath, "output");

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Consumer.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Consumer.svg");

            File.WriteAllText(dotPath, GetConsumerGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);
        }

        public static void CreateDependencyGraphvizForProject(DependencyNode startNode, string project)
        {
            var basePath = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
            basePath = Path.Combine(basePath, "output");

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Dependency.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Dependency.svg");

            File.WriteAllText(dotPath, GetDependencyGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);
        }

        public static void CreateFullGraphvizForProject(DependencyNode startNode, string project)
        {
            var basePath = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.Parent.FullName;
            basePath = Path.Combine(basePath, "output");

            var dotPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Full.dot");
            var svgPath = Path.Combine(basePath, $"{startNode.ClassName}-{project}-Full.svg");

            File.WriteAllText(dotPath, GetFullGraphvizString(startNode, project));

            GenerateSvg(dotPath, svgPath);
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

            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].RegistrationType;
            TraverseConsumerGraph(startNode, project, rootLifetime, currentPath, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void TraverseConsumerGraph(DependencyNode node, string project, LifetimeTypes rootLifetime, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            if (!node.RegistrationInfo.TryGetValue(project, out var registration)) return;
            if (path.Contains(node.Class, SymbolEqualityComparer.Default)) return;
            path.Push(node.Class);

            CreateNode(sb, node.ClassName, registration.RegistrationType);

            foreach (var dependant in node.DependedOnBy)
            {
                TraverseConsumerGraph(dependant, project, rootLifetime, path, sb);
            }

            foreach (var dependant in node.DependedOnBy)
            {
                if (!dependant.RegistrationInfo.TryGetValue(project, out var dependantReg)) continue;
                string label = "";
                if (dependantReg.RegistrationType > rootLifetime)
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


        public static string GetFullGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=LR;");

            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].RegistrationType;
            TraverseConsumerGraph(startNode, project, rootLifetime, currentPath, sb);
            TraverseDependencyGraph(startNode, project, rootLifetime, currentPath, sb);

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetDependencyGraphvizString(DependencyNode startNode, string project)
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph Dependencies {");
            sb.AppendLine("\t rankdir=LR;");

            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].RegistrationType;
            TraverseDependencyGraph(startNode, project, rootLifetime, currentPath, sb);

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

        private static void TraverseDependencyGraph(DependencyNode node, string project, LifetimeTypes rootLifetime, Stack<INamedTypeSymbol> path, StringBuilder sb, bool factorySubDependency = false)
        {
            if (path.Contains(node.Class, SymbolEqualityComparer.Default)) return;
            path.Push(node.Class);

            if (!node.RegistrationInfo.TryGetValue(project, out var projectRegistration))
            {
                //Create this node and edge from the parent perspective
                return;
            }

            //TODO do something with factory stuff
            factorySubDependency = factorySubDependency || projectRegistration.IsFactoryMethod;

            CreateNode(sb, node.ClassName, projectRegistration.RegistrationType);

            foreach (var dependency in node.DependsOn)
            {
                TraverseDependencyGraph(dependency, project, rootLifetime, path, sb, factorySubDependency);
            }

            var dependencies = GetReleventInterfaceAndImplementations(node.DependsOn, project);
            foreach (var dependency in dependencies)
            {
                string label;
                if (!dependency.RegistrationInfo.TryGetValue(project, out var dependencyReg))
                {
                    //Dependency was not registered for project. this is a runtime error probably
                    //If parent tree contains factory method this might not be issue
                    if (!factorySubDependency)
                    {
                        //dependency not registered create node and edge because it was skipped
                        var color = GetBackgroundColorForLifetime(LifetimeTypes.Unknown);
                        CreateNode(sb, dependency.ClassName, LifetimeTypes.Unknown);
                        label = $"[label=\"WARNING NOT REGISTERED\", color={color}, fontcolor={color}]";
                        CreateEdge(sb, node.ClassName, dependency.ClassName, label);
                    }

                    continue;
                }

                if (dependencyReg.RegistrationType < rootLifetime)
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

        private static string GetBackgroundColorForLifetime(LifetimeTypes lifetime)
        {
            return lifetime switch
            {
                LifetimeTypes.Transient => "\"#ccffff\"",  // Light Cyan
                LifetimeTypes.PerWebRequest => "\"#fff2cc\"",  // Light Yellow/Orange
                LifetimeTypes.Singleton => "\"#ffcccc\"",  // Light Red
                _ => "\"#ff00ff\""   // Default Magenta
            };
        }
        
        private static string GetTextColorForLifetime(LifetimeTypes lifetime)
        {
            return "black";
        }
    }
}
