using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class DependencyAnalyzer : BaseParser
    {
        private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> DependencyMap;
        private readonly SolutionAnalyzer SolutionAnalyzer;
        private readonly ManualResolutionParser ManualResolutionParser;
        public DependencyAnalyzer(SolutionAnalyzer solutionAnalyzer, ManualResolutionParser manualResolutionParser)
        {
            SolutionAnalyzer = solutionAnalyzer;
            ManualResolutionParser = manualResolutionParser;
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

                dependencies.AddRange(GetDependenciesFromConstructors(classSymbol.Constructors));
                dependencies.AddRange(GetManualResolvedDependenciesForClass(classSymbol));
               
                dependencyMap[classSymbol] = dependencies;
            }

            return dependencyMap;
        }

        private IEnumerable<INamedTypeSymbol> GetManualResolvedDependenciesForClass(INamedTypeSymbol classSymbol)
        {
            var comparer = new FullyQualifiedNameComparer();
            return ManualResolutionParser.ManuallyResolvedSymbols
                .Where(x => comparer.Equals(x.ContainingType, classSymbol))
                .Select(x => x.Type);
        }

        private List<INamedTypeSymbol> GetDependenciesFromConstructors(IEnumerable<IMethodSymbol> constructors)
        {
            var ret = new List<INamedTypeSymbol>();
            foreach (var constructor in constructors)
            {
                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Type is INamedTypeSymbol depType)
                    {
                        if (depType.Locations.Any(loc => loc.IsInSource))
                        {
                            ret.Add(depType);
                        }

                        if (depType.IsGenericType)
                        {
                            if (depType.TypeArguments.Length > 0 &&
                                (depType.Name is "IEnumerable" or "Lazy"))
                            {
                                foreach (var arg in depType.TypeArguments.OfType<INamedTypeSymbol>())
                                {
                                    if (arg.Locations.Any(loc => loc.IsInSource))
                                    {
                                        ret.Add(arg);
                                    }
                                }
                            }
                            else
                            {
                                ret.Add(depType);
                            }
                        }
                    }
                }
            }
            return ret;
        }

        public DependencyGraph BuildFullDependencyGraph()
        {
            var nodes = new List<DependencyNode>();
            foreach (var symbol in SolutionAnalyzer.AllTypes)
            {
                nodes.AddRange(CreateBaseNode(symbol));
            }

            WireDependencies(nodes);

            DedupeDependencies(nodes);

            return new DependencyGraph(nodes.ToList());
        }

        private void DedupeDependencies(List<DependencyNode> nodes)
        {
            var comparer = new RegInfoKeyComparer();

            foreach (var node in nodes)
            {
                node.DependsOn = node.DependsOn
                .DistinctBy(n => (n.ImplementationType, n.ServiceInterface, n.ProjectName, n.Lifetime), comparer)
                .ToList();

                node.DependedOnBy = node.DependedOnBy
                    .DistinctBy(n => (n.ImplementationType, n.ServiceInterface, n.ProjectName, n.Lifetime), comparer)
                    .ToList();
            }
        }

        private List<DependencyNode> CreateBaseNode(INamedTypeSymbol symbol)
        {
            var comparer = new FullyQualifiedNameComparer();
            var nodes = new List<DependencyNode>();

            if (symbol.TypeKind == TypeKind.Interface)
                return nodes;

            var allRegistrations = SolutionAnalyzer.GetRegistrationsForSymbol(symbol);
            if (allRegistrations.Count == 0)
            {
                nodes.Add(new DependencyNode
                {
                    ImplementationType = symbol,
                    ServiceInterface = null,
                    ProjectName = symbol.ContainingAssembly.Name,
                    ClassName = symbol.ToDisplayString(),// Fully qualified type name
                    Lifetime = LifetimeTypes.Unregistered
                });
                return nodes;
            }

            // Group registrations by (implementation type, service interface).
            // A single class can implement and be registered as multiple different services,
            // so each (impl, service) pair will produce its own node in the dependency graph.
            var regiGroups = allRegistrations.GroupBy(x => (
                        x.ImplementationType,
                        x.ServiceInterface,
                        x.ProjectName,
                        x.Lifetime
            ), new RegInfoKeyComparer());

            foreach ( var regiGroup in regiGroups)
            {
                if(regiGroup.Count() > 1)
                {
                    Console.WriteLine("Registration for same service/impl/project/lifestyle");
                    regiGroup.ToList().ForEach(x => 
                        Console.WriteLine($"Implementation {x.ImplementationType?.ToDisplayString()} " +
                        $"Interface {x.ServiceInterface?.ToDisplayString()} " +
                        $"Project {x.ProjectName} " +
                        $"Lifetime {x.Lifetime}"));
                }
                var reg = regiGroup.First();

                nodes.Add(new DependencyNode
                {
                    ImplementationType = symbol,
                    ServiceInterface = regiGroup.Key.ServiceInterface,
                    ClassName = symbol.ToDisplayString(),// Fully qualified type name
                    Lifetime = reg.Lifetime,
                    ProjectName = reg.ProjectName
                });
            }

            return nodes;
        }

        private void WireDependencies(
            List<DependencyNode> nodes)
        {
            var comparer = new FullyQualifiedNameComparer();

            //Interfaces dont have dependencies. Assume the dependantSymbol is a concrete class
            foreach (var (dependantSmybol, dependencies) in DependencyMap)
            {
                //find all nodes for the type
                List<DependencyNode> dependantNodes = nodes.Where(x => comparer.Equals(x.ImplementationType, dependantSmybol)).ToList();

                //interfaces wont have nodes
                if (dependantNodes.Count == 0)
                    continue;

                //for each dependency the class has, connect it to the nodes that represent those objects
                foreach (var dependencySymbol in dependencies)
                {
                    foreach (var dependantNode in dependantNodes)
                    {
                        //link the raw dependencies (interfaces / literal classes asked for)
                        dependantNode.RawDependencies = dependencies;

                        //find all nodes that could satisfy the dependency
                        var dependencyNodes = nodes
                                        .Where(x => x.ProjectName == dependantNode.ProjectName &&
                                                    x.SatisfiesDependency(dependencySymbol)
                                        ).ToList();
                            
                        dependantNode.DependsOn.AddRange(dependencyNodes);
                        dependencyNodes.ForEach(x => x.DependedOnBy.Add(dependantNode));
                    }
                }
            }
        }

        private sealed class RegInfoKeyComparer : IEqualityComparer<(INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime)>
        {
            public bool Equals(
                (INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime) x,
                (INamedTypeSymbol? ImplementationType, INamedTypeSymbol? ServiceInterface, string projectName, LifetimeTypes lifetime) y)
            {
                var comparer = new FullyQualifiedNameComparer();
                return comparer.Equals(x.ImplementationType, y.ImplementationType) &&
                        comparer.Equals(x.ServiceInterface, y.ServiceInterface) &&
                        x.lifetime == y.lifetime &&
                        x.projectName == y.projectName;

            }

            public int GetHashCode((INamedTypeSymbol?, INamedTypeSymbol?, string, LifetimeTypes) obj)
            {
                unchecked
                {
                    var cmp = new FullyQualifiedNameComparer();
                    var hash = 17;
                    hash = hash * 31 + cmp.GetHashCode(obj.Item1);
                    hash = hash * 31 + cmp.GetHashCode(obj.Item2);
                    hash = hash * 31 + obj.Item3.GetHashCode();
                    hash = hash * 31 + obj.Item4.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
