using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer
{
    public class DependencyNode
    {
        public INamedTypeSymbol Class { get; set; }
        public List<INamedTypeSymbol> DependsOn { get; set; } = [];
    }

    public class DependencyAnalyzer
    {
        public static List<DependencyNode> AnalyzeDependencies(IEnumerable<INamedTypeSymbol> classSymbols)
        {
            var graph = new List<DependencyNode>();

            foreach (var symbol in classSymbols)
            {
                var node = new DependencyNode
                {
                    Class = symbol,
                    DependsOn = new List<INamedTypeSymbol>()
                };

                foreach (var constructor in symbol.Constructors)
                {
                    foreach (var param in constructor.Parameters)
                    {

                        if (param.Type is INamedTypeSymbol namedType &&
                            namedType.Locations.Any(loc => loc.IsInSource))
                        {
                            node.DependsOn.Add(namedType);
                        }
                    }
                }

                graph.Add(node);
            }

            return graph;
        }
    }
}
