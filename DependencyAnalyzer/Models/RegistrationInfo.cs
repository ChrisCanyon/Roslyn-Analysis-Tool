using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Models
{
    public class RegistrationInfo
    {
        public INamedTypeSymbol? ImplementationType { get; set; }
        public INamedTypeSymbol? ServiceType { get; set; }
        public required string ProjectName { get; set; }
        public LifetimeTypes Lifetime { get; set; }
        public bool IsFactoryResolved { get; set; } = false;
        //if this is true we assume ANY implementation of the interface is valid
        public bool UnresolvableImplementation { get; set; } = false;
    }
}
