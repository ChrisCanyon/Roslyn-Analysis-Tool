using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer
{
    public static class ExpressionTraversalHelper
    {
        public static InvocationExpressionSyntax? FindAncestorInvocationInChain(ExpressionSyntax expression, string methodName)
        {
            SyntaxNode current = expression;

            while (true)
            {
                // Skip MemberAccess nodes
                current = current.Parent;
                while (current is MemberAccessExpressionSyntax)
                    current = current.Parent;

                if (current is InvocationExpressionSyntax invocation &&
                    invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var currentMethodName = memberAccess.Name.Identifier.Text;

                    if (currentMethodName == methodName)
                    {
                        return invocation;
                    }
                }
                else
                {
                    break;
                }
            }
            return null;
        }
    }
}
