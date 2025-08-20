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

        public RawDependency GetRawDependency(DependencyNode node)
        {
            return RawDependencies.First(x => node.SatisfiesDependency(x.Type));
        }

        public bool SatisfiesDependency(INamedTypeSymbol requested)
        {
            var cmp = new FullyQualifiedNameComparer();

            // Interface request
            if (requested.TypeKind == TypeKind.Interface)
            {
                if (ServiceInterface is null) return false;

                if (!requested.IsUnboundGenericType)
                {
                    // Closed request: exact match OR open registration with same original def
                    if (cmp.Equals(ServiceInterface, requested)) return true; // exact closed
                    if (ServiceInterface.IsUnboundGenericType &&
                        cmp.Equals(ServiceInterface.OriginalDefinition, requested.OriginalDefinition))
                        return true; // open generic registration satisfies closed
                    return false;
                }
                else
                {
                    // Open request: match by original definition
                    return cmp.Equals(ServiceInterface.OriginalDefinition, requested.OriginalDefinition);
                }
            }

            // Class request
            if (requested.TypeKind == TypeKind.Class)
            {
                if (!requested.IsUnboundGenericType)
                {
                    if (cmp.Equals(ImplementationType, requested)) return true; // exact closed impl
                    if (ImplementationType.IsUnboundGenericType &&
                        cmp.Equals(ImplementationType.OriginalDefinition, requested.OriginalDefinition))
                        return true; // open impl satisfies closed
                    return false;
                }
                else
                {
                    return cmp.Equals(ImplementationType.OriginalDefinition, requested.OriginalDefinition);
                }
            }

            return false;
        }

        private List<INamedTypeSymbol>? _unsatisfiedDependencies = null;

        public List<INamedTypeSymbol> UnsatisfiedDependencies()
        {
            if (_unsatisfiedDependencies != null) return _unsatisfiedDependencies;

            var ret = new List<INamedTypeSymbol>();

            foreach (var rawDep in RawDependencies)
            {
                if (!DependsOn.Any(x => x.SatisfiesDependency(rawDep.Type)))
                {
                    ret.Add(rawDep.Type);
                }
            }

            _unsatisfiedDependencies = ret;
            return ret;
        }
    }
}
