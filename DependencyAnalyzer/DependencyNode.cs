using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.Json.Serialization;

namespace DependencyAnalyzer
{
    public enum LifetimeTypes
    {
        Transient,
        PerWebRequest,
        Singleton
    }

    public class RegistrationInfo
    {
        public INamedTypeSymbol? Implementation { get; set; } 
        public INamedTypeSymbol? Interface { get; set; }
        public required string ProjectName { get; set; }
        public LifetimeTypes RegistrationType { get; set; }
        public bool IsFactoryMethod { get; set; } = false;
        public string Print()
        {
            var interfaceName = Interface?.ToDisplayString() ?? "(none)";
            return
                $"- Project: {ProjectName}\n" +
                $"  Interface: {interfaceName}\n" +
                $"  Lifetime: {RegistrationType}";
        }
    }

    public class DependencyNode
    {
        [JsonIgnore]
        public required INamedTypeSymbol Class { get; set; }

        // For serialization / external analysis
        public required string ProjectName { get; set; }
        public required string ClassName { get; set; }

        public Dictionary<string, RegistrationInfo> RegistrationInfo { get; set; } = []; //<projectName, RegistrationInfo>

        public List<DependencyNode> DependsOn { get; set; } = [];
        public List<DependencyNode> DependedOnBy { get; set; } = [];
        public List<DependencyNode> Implements { get; set; } = [];
        public List<DependencyNode> ImplementedBy { get; set; } = [];

        public bool IsInterface => Class.TypeKind == TypeKind.Interface;

        
    }
}
