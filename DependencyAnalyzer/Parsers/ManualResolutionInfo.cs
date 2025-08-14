using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.Parsers
{
    public enum ManualLifetimeInteractionKind
    {
        Resolve,
        Dispose
    }
    public record ManualLifetimeInteractionInfo(
        INamedTypeSymbol Type,
        INamedTypeSymbol ContainingType,
        string CodeSnippet,
        string Project,
        string InvocationPath,
        ManualLifetimeInteractionKind Kind
    );

    record InvocationChainFromRoot(
            INamedTypeSymbol RootClass,
            IMethodSymbol RootMethod,
            InvocationExpressionSyntax RootInvocation,
            string Project,
            string InvocationPath
        );

    record InstallInvocationContext(
            InvocationExpressionSyntax Invocation,
            SemanticModel SemanticModel,
            string ProjectName
        );
}
