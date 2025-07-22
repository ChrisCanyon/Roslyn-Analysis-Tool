using Microsoft.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace DependencyAnalyzer
{

    public class DependencyNode
    {
        [JsonIgnore]
        public required INamedTypeSymbol Class { get; set; }

        // For serialization / external analysis
        public required string ProjectName { get; set; }
        public required string ClassName { get; set; } 

        public string? Lifetime { get; set; } // e.g. "Singleton", "Scoped", "Transient", or null

        public List<DependencyNode> DependsOn { get; set; } = [];
        public List<DependencyNode> DependedOnBy { get; set; } = [];
        public List<DependencyNode> Implements { get; set; } = [];
        public List<DependencyNode> ImplementedBy { get; set; } = [];

        public bool IsInterface => Class.TypeKind == TypeKind.Interface;

        public string PrintDependencyTree()
        {
            var sb = new StringBuilder();
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependenciesRecursive(this, "", true, currentPath, sb);
            return sb.ToString();
        }
        private void PrintDependenciesRecursive(DependencyNode node, string prefix, bool isLast, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            var marker = isLast ? "└─ " : "├─ ";
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var deps = node.DependsOn.OrderBy(d => d.ClassName).ToList();
            for (int i = 0; i < deps.Count; i++)
            {
                var isLastChild = (i == deps.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependenciesRecursive(deps[i], childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public string PrintConsumerTree()
        {
            var sb = new StringBuilder();
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependedOnByRecursive(this, "", true, currentPath, sb);
            return sb.ToString();
        }

        private void PrintDependedOnByRecursive(DependencyNode node, string prefix, bool isLast, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            var marker = isLast ? "└─ " : "├─ ";
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var dependents = node.DependedOnBy.OrderBy(d => d.ClassName).ToList();
            for (int i = 0; i < dependents.Count; i++)
            {
                var isLastChild = (i == dependents.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependedOnByRecursive(dependents[i], childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }
    }

    public class DependencyAnalyzer
    {
        public static Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> GetClassDependencies(IEnumerable<INamedTypeSymbol> classSymbols)
        {
            var comparer = SymbolEqualityComparer.Default;
            var dependencyMap = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);

            foreach (var classSymbol in classSymbols)
            {
                var dependencies = new List<INamedTypeSymbol>();

                foreach (var constructor in classSymbol.Constructors)
                {
                    foreach (var parameter in constructor.Parameters)
                    {
                        if (parameter.Type is INamedTypeSymbol depType &&
                            depType.Locations.Any(loc => loc.IsInSource)) // Only include source-defined dependencies
                        {
                            dependencies.Add(depType);
                        }
                    }
                }

                dependencyMap[classSymbol] = dependencies;
            }

            return dependencyMap;
        }

        public static Dictionary<INamedTypeSymbol, DependencyNode> BuildFullDependencyGraph(
            Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> dependencyMap,
            IEnumerable<INamedTypeSymbol> allSymbols)
        {
            var nodeMap = CreateBaseNodes(allSymbols);

            WireDependencies(dependencyMap, nodeMap);

            WireInterfaceImplementations(allSymbols, nodeMap);

            ExpandInterfaces(nodeMap);

            return nodeMap;
        }

        private static void ExpandInterfaces(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            ExpandInterfaceDownstreamEdges(nodeMap);
            ExpandInterfaceUpstreamEdges(nodeMap);
        }

        public class FullyQualifiedNameComparer : IEqualityComparer<INamedTypeSymbol>
        {
            public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
            {
                if (x is null || y is null)
                    return false;

                return GetKey(x) == GetKey(y);
            }

            public int GetHashCode(INamedTypeSymbol obj)
            {
                return GetKey(obj).GetHashCode();
            }

            private string GetKey(INamedTypeSymbol symbol)
            {
                return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }

        private static Dictionary<INamedTypeSymbol, DependencyNode> CreateBaseNodes(IEnumerable<INamedTypeSymbol> allSymbols)
        {
            var comparer = new FullyQualifiedNameComparer();
            var nodeMap = new Dictionary<INamedTypeSymbol, DependencyNode>(comparer);

            foreach (var symbol in allSymbols)
            {
                var node = new DependencyNode
                {
                    Class = symbol,
                    ClassName = symbol.ToDisplayString(),                   // Fully qualified type name
                    ProjectName = symbol.ContainingAssembly.Name,           // Approximate project name
                    Lifetime = GetLifetimeFromNameOrAttributes(symbol)      // Placeholder for now
                };

                nodeMap[symbol] = node;
            }

            return nodeMap;
        }

        //TODO actually look at registration
        private static string? GetLifetimeFromNameOrAttributes(INamedTypeSymbol symbol)
        {
            var name = symbol.Name.ToLowerInvariant();

            if (name.Contains("singleton")) return "Singleton";
            if (name.Contains("scoped")) return "Scoped";
            if (name.Contains("transient")) return "Transient";

            return null;
        }

        private static void WireDependencies(
            Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> dependencyMap,
            Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            var comparer = SymbolEqualityComparer.Default;

            foreach (var (classSymbol, dependencies) in dependencyMap)
            {
                if (!nodeMap.TryGetValue(classSymbol, out var classNode))
                    continue;

                foreach (var dependencySymbol in dependencies)
                {
                    if (!nodeMap.TryGetValue(dependencySymbol, out var dependencyNode))
                        continue;

                    // Wire the edge
                    classNode.DependsOn.Add(dependencyNode);
                    dependencyNode.DependedOnBy.Add(classNode);
                }
            }
        }

        private static void WireInterfaceImplementations(
            IEnumerable<INamedTypeSymbol> allSymbols,
            Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            foreach (var classSymbol in allSymbols)
            {
                // Skip interfaces and non-source symbols
                if (classSymbol.TypeKind != TypeKind.Class || !classSymbol.Locations.Any(loc => loc.IsInSource))
                    continue;

                if (!nodeMap.TryGetValue(classSymbol, out var classNode))
                    continue;

                foreach (var interfaceSymbol in classSymbol.AllInterfaces)
                {
                    if (!nodeMap.TryGetValue(interfaceSymbol, out var interfaceNode))
                        continue;

                    classNode.Implements.Add(interfaceNode);
                    interfaceNode.ImplementedBy.Add(classNode);
                }
            }
        }

        private static void ExpandInterfaceDownstreamEdges(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            foreach (var node in nodeMap.Values)
            {
                var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                var expanded = new List<DependencyNode>();

                foreach (var dependency in node.DependsOn)
                {
                    // Add the interface or class
                    if (seen.Add(dependency.Class))
                        expanded.Add(dependency);

                    // If it's an interface, add its implementations directly after
                    if (dependency.IsInterface)
                    {
                        foreach (var impl in dependency.ImplementedBy)
                        {
                            if (seen.Add(impl.Class))
                                expanded.Add(impl);
                        }
                    }
                }

                node.DependsOn = expanded;
            }
        }

        private static void ExpandInterfaceUpstreamEdges(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            foreach (var node in nodeMap.Values)
            {
                foreach (var dependency in node.DependsOn)
                {
                    if (dependency.IsInterface && dependency.ImplementedBy.Count > 0)
                    {
                        foreach (var implementation in dependency.ImplementedBy)
                        {
                            implementation.DependedOnBy.Add(node);
                        }
                    }
                }
            }
        }
    }
}
