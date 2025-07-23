using Microsoft.CodeAnalysis;
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

        public string PrintDependencyTree()
        {
            var sb = new StringBuilder();
            var currentPath = new Stack<INamedTypeSymbol>();
            PrintDependenciesRecursive(this, "", true, currentPath, sb);
            return sb.ToString();
        }
        private void PrintDependenciesRecursive(DependencyNode node, string prefix, bool isLast, Stack<INamedTypeSymbol> path, StringBuilder sb)
        {
            string marker = prefix == "" ? "" : (isLast ? "└─ " : "├─ ");
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var deps = node.DependsOn.ToList();
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
            string marker = prefix == "" ? "" : (isLast ? "╘═ " : "╞═ ");
            var cycle = path.Contains(node.Class, SymbolEqualityComparer.Default) ? " ↩ (cycle)" : "";
            sb.AppendLine($"{prefix}{marker}{node.ClassName}{cycle}");

            if (!string.IsNullOrEmpty(cycle))
                return;

            path.Push(node.Class);

            var dependents = node.DependedOnBy.ToList();
            for (int i = 0; i < dependents.Count; i++)
            {
                var isLastChild = (i == dependents.Count - 1);
                var childPrefix = prefix + (isLast ? "   " : "│  ");
                PrintDependedOnByRecursive(dependents[i], childPrefix, isLastChild, path, sb);
            }

            path.Pop();
        }

        public string PrintRegistrations()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Class: {ClassName}");
            sb.AppendLine();
            sb.AppendLine("Registrations:");

            if (RegistrationInfo.Count == 0)
            {
                sb.AppendLine("  (none)");
                return sb.ToString();
            }

            foreach (var (project, registartion) in RegistrationInfo)
            {
                sb.AppendLine(registartion.Print());
            }
            return sb.ToString();
        }
    }
}
