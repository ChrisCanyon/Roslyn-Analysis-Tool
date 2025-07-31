using Microsoft.CodeAnalysis;

namespace DependencyAnalyzer.Parsers
{
    public enum ManualLifetimeInteractionKind
    {
        Resolve,
        Dispose
    }
    public record ManualLifetimeInteractionInfo(
        INamedTypeSymbol Type,
        string Project,
        string File,
        string CodeSnippet,
        ManualLifetimeInteractionKind Kind
    );
}
