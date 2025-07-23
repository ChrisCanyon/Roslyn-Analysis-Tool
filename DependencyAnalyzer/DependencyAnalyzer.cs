using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class DependencyAnalyzer
    {
        public static Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> GetClassDependencies(SolutionAnalyzer solutionAnalyzer)
        {
            var classSymbols = solutionAnalyzer.AllTypes;

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
            SolutionAnalyzer solutionAnalyzer)
        {
            var allSymbols = solutionAnalyzer.AllTypes;

            var nodeMap = CreateBaseNodes(allSymbols, solutionAnalyzer);

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

        private static Dictionary<INamedTypeSymbol, DependencyNode> CreateBaseNodes(IEnumerable<INamedTypeSymbol> allSymbols, SolutionAnalyzer solutionAnalyzer)
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
                    RegistrationInfo = solutionAnalyzer.GetRegistrationsForSymbol(symbol)
                };

                nodeMap[symbol] = node;
            }

            return nodeMap;
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
