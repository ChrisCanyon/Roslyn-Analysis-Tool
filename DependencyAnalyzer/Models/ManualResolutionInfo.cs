using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.Parsers
{
    public enum ManualResolveUsage
    {
        Local,
        Ambiguous,
        Stored
    }

    public record ManualResolveInfo
    {
        public required INamedTypeSymbol ResolvedType { get; init; }
        public required INamedTypeSymbol ContainingType { get; init; }
        public required string CodeSnippet { get; init; }
        public required string Project { get; init; }
        public required string InvocationPath { get; init; }
        public required ManualResolveUsage Usage { get; init; }
    }

    public record ManualDisposeInfo
    {
        public required INamedTypeSymbol DisposedType { get; init; }
        public required INamedTypeSymbol ContainingType { get; init; }
        public required string CodeSnippet { get; init; }
        public required string Project { get; init; }
        public required string InvocationPath { get; init; }
    }

    public record InvocationChainFromRoot
    {
        public required INamedTypeSymbol RootClass { get; init; }
        public required IMethodSymbol RootMethod { get; init; }
        public required InvocationExpressionSyntax RootInvocation { get; init; }
        public required string Project { get; init; }
        public required string InvocationPath { get; init; }
    }

    public record InstallInvocationContext
    {
        public required InvocationExpressionSyntax Invocation { get; init; }
        public required SemanticModel SemanticModel { get; init; }
        public required string ProjectName { get; init; }
    }
}
