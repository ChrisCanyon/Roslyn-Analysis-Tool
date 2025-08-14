using Microsoft.CodeAnalysis;
using System.Text.Json.Serialization;

namespace DependencyAnalyzer
{
    //there are ordered by shortest to longest lived
    //a simple > or < comparison tells you if you are longer or shorter lived
    public enum LifetimeTypes
    {
        Controller,
        Transient,
        PerWebRequest,
        Singleton,
        Unregistered
    }

    public class RegistrationInfo
    {
        public INamedTypeSymbol? ImplementationType { get; set; } 
        public INamedTypeSymbol? ServiceInterface { get; set; }
        public required string ProjectName { get; set; }
        public LifetimeTypes Lifetime { get; set; }
        public bool IsFactoryResolved { get; set; } = false;
        //if this is true we assume ANY implementation of the interface is valid
        public bool UnresolvableImplementation { get; set; } = false;
    }

    public class DependencyNode
    {
        public required INamedTypeSymbol ImplementationType { get; set; }
        public required INamedTypeSymbol? ServiceInterface { get; set; }
        public required string ProjectName { get; set; }
        public required string ClassName { get; set; }
        public LifetimeTypes Lifetime { get; set; }
        public List<DependencyNode> DependsOn { get; set; } = [];
        public List<DependencyNode> DependedOnBy { get; set; } = [];
        public List<INamedTypeSymbol> RawDependencies { get; set; } = [];

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
    }
}
