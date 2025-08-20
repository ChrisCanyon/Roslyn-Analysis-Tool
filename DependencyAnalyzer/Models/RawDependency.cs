using DependencyAnalyzer.Parsers;
using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Models
{
    public enum DependencySource
    {
        Constructor,
        Manual_Local,
        Manual_Stored,
        Manual_Ambiguous
    }

    public class RawDependency
    {
        public INamedTypeSymbol Type { get; }
        public DependencySource Source { get; }

        private RawDependency(INamedTypeSymbol type, DependencySource source)
        {
            Type = type;
            Source = source;
        }

        public static RawDependency FromManualResolution(ManualResolveInfo info)
            => new(
                info.ResolvedType,
                info.Usage switch
                {
                    ManualResolveUsage.Local => DependencySource.Manual_Local,
                    ManualResolveUsage.Stored => DependencySource.Manual_Stored,
                    _ => DependencySource.Manual_Ambiguous
                });

        public static RawDependency FromConstructor(INamedTypeSymbol type)
            => new RawDependency(type, DependencySource.Constructor);
    }
}
