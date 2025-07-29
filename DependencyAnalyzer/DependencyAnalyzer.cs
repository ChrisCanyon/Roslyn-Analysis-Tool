using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class DependencyAnalyzer
    {
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> DependencyMap;
        private readonly SolutionAnalyzer SolutionAnalyzer;
        public DependencyAnalyzer(SolutionAnalyzer solutionAnalyzer) {
            SolutionAnalyzer = solutionAnalyzer;
            DependencyMap = GetClassDependencies();
        }

        private Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> GetClassDependencies()
        {
            var classSymbols = SolutionAnalyzer.AllTypes;

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

        //TODO make node map a class variable
        public DependencyGraph BuildFullDependencyGraph()
        {
            var nodeMap = CreateBaseNodes();

            WireDependencies(nodeMap);

            WireInterfaceImplementations(nodeMap);

            ExpandInterfaces(nodeMap);

            return new DependencyGraph(nodeMap.Values.ToList());
        }

        private void ExpandInterfaces(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            ExpandInterfaceDownstreamEdges(nodeMap);
            ExpandInterfaceUpstreamEdges(nodeMap);
        }

        private Dictionary<INamedTypeSymbol, DependencyNode> CreateBaseNodes()
        {
            var comparer = new FullyQualifiedNameComparer();
            var nodeMap = new Dictionary<INamedTypeSymbol, DependencyNode>(comparer);

            foreach (var symbol in SolutionAnalyzer.AllTypes)
            {
                var node = new DependencyNode
                {
                    Class = symbol,
                    ClassName = symbol.ToDisplayString(),                   // Fully qualified type name
                    RegistrationInfo = SolutionAnalyzer.GetRegistrationsForSymbol(symbol)
                };

                nodeMap[symbol] = node;
            }

            return nodeMap;
        }

        private void WireDependencies(
            Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            var comparer = SymbolEqualityComparer.Default;

            foreach (var (classSymbol, dependencies) in DependencyMap)
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

        private void WireInterfaceImplementations(
            Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
        {
            foreach (var classSymbol in SolutionAnalyzer.AllTypes)
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

        private void ExpandInterfaceDownstreamEdges(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
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

        private void ExpandInterfaceUpstreamEdges(Dictionary<INamedTypeSymbol, DependencyNode> nodeMap)
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
