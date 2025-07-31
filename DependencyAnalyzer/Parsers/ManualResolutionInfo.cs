using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Parsers
{
    public record ManualResolutionInfo(
            INamedTypeSymbol ResolvedType,
            string Project,
            string File,
            string CodeSnippet
    );
}
