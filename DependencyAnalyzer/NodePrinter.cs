using Microsoft.CodeAnalysis;
using System.Text;
using System.Xml.Linq;

namespace DependencyAnalyzer
{
    public class NodePrinter
    {
        public static string PrintDependencyTree(DependencyNode startNode)
        {
            var sb = new StringBuilder();
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependenciesRecursive(startNode, "", true, currentPath, sb);
            return sb.ToString();
        }
        private static void PrintDependenciesRecursive(DependencyNode node, string prefix, bool isLast, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            string marker = prefix == "" ? "" : (isLast ? "└─ " : "├─ ");
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var deps = node.DependsOn.ToList();
            for (int i = 0; i < deps.Count; i++)
            {
                var isLastChild = (i == deps.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependenciesRecursive(deps[i], childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public static ColoredStringBuilder PrintConsumerTreeForProject(DependencyNode startNode, string project)
        {
            var sb = new ColoredStringBuilder();
            sb.AppendLine($"OBJECTS DEPENDENT ON {startNode.ClassName} FOR PROJECT {project}", ConsoleColor.Blue);
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependedOnByForProject(startNode, project, "", true, currentPath, sb);
            return sb;
        }

        private static void PrintDependedOnByForProject(DependencyNode node, string project, string prefix, bool isLast, Stack<INamedTypeSymbol> path, ColoredStringBuilder sb)
        {
            var projectRegistration = node.RegistrationInfo.Values.FirstOrDefault(x => x.ProjectName == project);
            if (projectRegistration == null) return;

            ConsoleColor consoleColor;
            switch (projectRegistration.RegistrationType)
            {
                case LifetimeTypes.Transient:
                    consoleColor = ConsoleColor.Green;
                    break;
                case LifetimeTypes.PerWebRequest:
                    consoleColor = ConsoleColor.Yellow;
                    break;
                default:
                    consoleColor = ConsoleColor.Red;
                    break;
            }


            string marker = prefix == "" ? "" : (isLast ? "╘═ " : "╞═ ");
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}", consoleColor);

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var dependents = node.DependedOnBy.ToList();
            for (int i = 0; i < dependents.Count; i++)
            {
                var isLastChild = (i == dependents.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependedOnByForProject(dependents[i], project, childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }
        public static string PrintConsumerTree(DependencyNode startNode)
        {
            var sb = new StringBuilder();
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependedOnByRecursive(startNode, "", true, currentPath, sb);
            return sb.ToString();
        }

        private static void PrintDependedOnByRecursive(DependencyNode node, string prefix, bool isLast, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            string marker = prefix == "" ? "" : (isLast ? "╘═ " : "╞═ ");
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var dependents = node.DependedOnBy.ToList();
            for (int i = 0; i < dependents.Count; i++)
            {
                var isLastChild = (i == dependents.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependedOnByRecursive(dependents[i], childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public static string PrintRegistrations(DependencyNode node)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Class: {node.ClassName}");
            sb.AppendLine();
            sb.AppendLine("Registrations:");

            if (node.RegistrationInfo.Count == 0)
            {
                sb.AppendLine("  (none)");
                return sb.ToString();
            }

            foreach (var (project, registartion) in node.RegistrationInfo)
            {
                sb.AppendLine(registartion.Print());
            }
            return sb.ToString();
        }
    }
}
