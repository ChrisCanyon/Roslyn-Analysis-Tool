using DependencyAnalyzer.Comparers;
using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Visualizers
{
    public class NodePrinter
    {
        public static ColoredStringBuilder PrintDependencyTreeForProject(DependencyNode startNode, string project)
        {
            var sb = new ColoredStringBuilder();
            sb.AppendLine($"OBJECTS {startNode.ClassName} IS DEPENDENT ON FOR PROJECT {project}", ConsoleColor.Cyan);
            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.Lifetime;
            TraverseDependencyGraph(startNode, project, rootLifetime, "", true, currentPath, sb);
            return sb;
        }

        private static void TraverseDependencyGraph(DependencyNode currentNode,
            string project,
            LifetimeTypes parentLifetime,
            string prefix,
            bool isLast,
            Stack<INamedTypeSymbol> path,
            ColoredStringBuilder sb,
            bool ambiguousRegistrationSubDependency = false)
        {
            string marker = prefix == "" ? "" : isLast ? "└─ " : "├─ ";
            var cycle = path.Contains(currentNode.ImplementationType, new FullyQualifiedNameComparer()) ? " ↩ (cycle)" : "";

            ConsoleColor consoleColor = ConsoleColor.Green;
            if (currentNode.Lifetime < parentLifetime)
            {
                consoleColor = ConsoleColor.Red;
            }

            sb.Append($"{prefix}{marker}{currentNode.ClassName}{cycle}", consoleColor);
            var lifestyleText = $" [{currentNode.Lifetime}]";
            sb.AppendLine(lifestyleText, GetColorForLifetime(currentNode.Lifetime));

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(currentNode.ImplementationType);

            for (int i = 0; i < currentNode.DependsOn.Count; i++)
            {
                var isLastChild = i == currentNode.DependsOn.Count - 1;
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                TraverseDependencyGraph(currentNode.DependsOn[i], project, currentNode.Lifetime, childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public static ColoredStringBuilder PrintConsumerTreeForProject(DependencyNode startNode, string project)
        {
            var sb = new ColoredStringBuilder();
            sb.AppendLine($"OBJECTS DEPENDENT ON {startNode.ClassName} FOR PROJECT {project}", ConsoleColor.Cyan);
            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.Lifetime;
            TraverseConsumerGraph(startNode, project, rootLifetime, "", true, currentPath, sb);
            return sb;
        }

        private static void TraverseConsumerGraph(DependencyNode currentNode, string project, LifetimeTypes rootLifetime, string prefix, bool isLast, Stack<INamedTypeSymbol> path, ColoredStringBuilder sb)
        {
            ConsoleColor consoleColor = ConsoleColor.Green;
            if (currentNode.Lifetime > rootLifetime)
            {
                consoleColor = ConsoleColor.Red;
            }
            if(currentNode.Lifetime == LifetimeTypes.Controller)
            {
                consoleColor = ConsoleColor.Gray;
            }

            string marker = prefix == "" ? "" : isLast ? "╘═ " : "╞═ ";
            var cycle = path.Contains(currentNode.ImplementationType, new FullyQualifiedNameComparer()) ? " ↩ (cycle)" : "";
            sb.Append($"{prefix}{marker}{currentNode.ClassName}{cycle}", consoleColor);
            var lifestyleText = $" [{currentNode.Lifetime}]";
            sb.AppendLine(lifestyleText, GetColorForLifetime(currentNode.Lifetime));

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(currentNode.ImplementationType);

            var dependants = currentNode.DependedOnBy.ToList();
            for (int i = 0; i < dependants.Count; i++)
            {
                var isLastChild = i == dependants.Count - 1;
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                TraverseConsumerGraph(dependants[i], project, rootLifetime, childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        private static ConsoleColor GetColorForLifetime(LifetimeTypes lifetime)
        {
            switch (lifetime)
            {
                case LifetimeTypes.Transient:
                    return ConsoleColor.Cyan;
                case LifetimeTypes.PerWebRequest:
                    return ConsoleColor.Blue;
                case LifetimeTypes.Singleton:
                    return ConsoleColor.DarkYellow;
                case LifetimeTypes.Controller:
                    return ConsoleColor.White;
                default:
                    return ConsoleColor.DarkYellow;
            }
        }
    }
}
