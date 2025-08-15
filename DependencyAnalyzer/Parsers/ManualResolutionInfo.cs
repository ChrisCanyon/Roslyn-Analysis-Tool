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
        INamedTypeSymbol Type, //the type disposed/resolved
        INamedTypeSymbol ContainingType, //the class the dispose/resolve was contained in
        string Project,
        string CodeSnippet, 
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
