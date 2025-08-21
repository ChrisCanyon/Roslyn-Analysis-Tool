using DependencyAnalyzer.Comparers;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Models
{
    public class DependencyNode
    {
        public required INamedTypeSymbol ImplementationType { get; set; }
        public required INamedTypeSymbol? ServiceInterface { get; set; }
        public required string ProjectName { get; set; }
        public LifetimeTypes Lifetime { get; set; }
        public required string ClassName { get; set; }
        public List<DependencyNode> DependsOn { get; set; } = [];
        public List<DependencyNode> DependedOnBy { get; set; } = [];
        public List<RawDependency> RawDependencies { get; set; } = [];

        //internal cache fields for extension methods
        internal List<INamedTypeSymbol>? _unsatisfiedDependencies = null;
        internal List<INamedTypeSymbol>? _satisfiedDependencies = null;
        internal List<ISymbol>? _potentialStateFields = null;
    }
}
