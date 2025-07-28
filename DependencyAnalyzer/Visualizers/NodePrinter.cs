using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using System.Text;

namespace DependencyAnalyzer.Visualizers
{
    public class NodePrinter
    {
        public static ColoredStringBuilder PrintDependencyTreeForProject(DependencyNode startNode, string project)
        {
            var sb = new ColoredStringBuilder();
            sb.AppendLine($"OBJECTS {startNode.ClassName} IS DEPENDENT ON FOR PROJECT {project}", ConsoleColor.Blue);
            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].RegistrationType;
            TraverseDependencyGraph(startNode, project, rootLifetime, "", true, currentPath, sb);
            return sb;
        }

        

        private static void TraverseDependencyGraph(DependencyNode node,
            string project,
            LifetimeTypes rootLifetime,
            string prefix,
            bool isLast,
            Stack<INamedTypeSymbol> path,
            ColoredStringBuilder sb,
            bool ambiguousRegistrationSubDependency = false)
        {
            string marker = prefix == "" ? "" : isLast ? "└─ " : "├─ ";
            var cycle = path.Contains(node.Class, new FullyQualifiedNameComparer()) ? " ↩ (cycle)" : "";

            if (!node.RegistrationInfo.TryGetValue(project, out var projectRegistration))
            {
                // This dependency was not registered for this project.
                // In most cases, this is likely a runtime resolution failure.
                // However, if this node is part of a registration with an unresolvable implementation
                // (e.g., a factory returning an unknown type), this missing registration might be acceptable.
                if (!ambiguousRegistrationSubDependency)
                {
                    var warn = "[WARN] NOT REGISTERED IN PROJECT";
                    sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}{warn}", ConsoleColor.Magenta);
                }
                return;
            }

            ConsoleColor consoleColor = ConsoleColor.Green;
            if (projectRegistration.RegistrationType < rootLifetime)
            {
                consoleColor = ConsoleColor.Red;
            }

            ambiguousRegistrationSubDependency = ambiguousRegistrationSubDependency || projectRegistration.UnresolvableImplementation;

            // If this is a sub-dependency of a registration with an unresolvable implementation,
            // we can't be certain this dependency is actually used in the project.
            // Highlight it as ambiguous (e.g., could be conditionally resolved at runtime).
            if (ambiguousRegistrationSubDependency)
            {
                consoleColor = ConsoleColor.Yellow;
            } 

            var implementationNote = projectRegistration.UnresolvableImplementation ? " [Ambiguous]" : "";
            var factoryNote = projectRegistration.IsFactoryResolved ? " [Factory Resolved]" : "";
            sb.Append($"{prefix}{marker}{node.ClassName}{cycle}{factoryNote}{implementationNote}", consoleColor);
            var lifestyleText = $" [{projectRegistration.RegistrationType}]";
            sb.AppendLine(lifestyleText, GetColorForLifetime(projectRegistration.RegistrationType));

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var deps = GetReleventInterfaceAndImplementations(node.DependsOn, project);

            for (int i = 0; i < deps.Count; i++)
            {
                var isLastChild = i == deps.Count - 1;
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                TraverseDependencyGraph(deps[i], project, rootLifetime, childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public static ColoredStringBuilder PrintConsumerTreeForProject(DependencyNode startNode, string project)
        {
            var sb = new ColoredStringBuilder();
            sb.AppendLine($"OBJECTS DEPENDENT ON {startNode.ClassName} FOR PROJECT {project}", ConsoleColor.Blue);
            var currentPath = new Stack<INamedTypeSymbol>();
            var rootLifetime = startNode.RegistrationInfo[project].RegistrationType;
            TraverseConsumerGraph(startNode, project, rootLifetime, "", true, currentPath, sb);
            return sb;
        }

        private static void TraverseConsumerGraph(DependencyNode node, string project, LifetimeTypes rootLifetime, string prefix, bool isLast, Stack<INamedTypeSymbol> path, ColoredStringBuilder sb)
        {
            if (!node.RegistrationInfo.TryGetValue(project, out var projectRegistration)) return;

            ConsoleColor consoleColor = ConsoleColor.Green;
            if (projectRegistration.RegistrationType > rootLifetime)
            {
                consoleColor = ConsoleColor.Red;
            }

            string marker = prefix == "" ? "" : isLast ? "╘═ " : "╞═ ";
            var cycle = path.Contains(node.Class, new FullyQualifiedNameComparer()) ? " ↩ (cycle)" : "";
            sb.Append($"{prefix}{marker}{node.ClassName}{cycle}", consoleColor);
            var lifestyleText = $" [{projectRegistration.RegistrationType}]";
            sb.AppendLine(lifestyleText, GetColorForLifetime(projectRegistration.RegistrationType));

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var dependants = node.DependedOnBy.ToList();
            for (int i = 0; i < dependants.Count; i++)
            {
                var isLastChild = i == dependants.Count - 1;
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                TraverseConsumerGraph(dependants[i], project, rootLifetime, childPrefix, isLastChild, path, sb);
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

        //TODO MAKE THIS COMMON WITH GRAPHVIZ CONVERTER
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
                default:
                    return ConsoleColor.DarkYellow;
            }
        }
    }
}
