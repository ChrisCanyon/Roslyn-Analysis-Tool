using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.RegistrationParsers;

public abstract class BaseParser
{
    protected static IEnumerable<INamedTypeSymbol> GetFactoryReturnTypes(ExpressionSyntax factoryArg, SemanticModel model)
    {
        var returnTypes = new List<INamedTypeSymbol>();

        if (factoryArg is LambdaExpressionSyntax lambda)
        {
            // Handle ternary: () => condition ? new A() : new B()
            if (lambda.Body is ConditionalExpressionSyntax ternary)
            {
                var type1 = model.GetTypeInfo(ternary.WhenTrue).Type as INamedTypeSymbol;
                var type2 = model.GetTypeInfo(ternary.WhenFalse).Type as INamedTypeSymbol;

                if (type1?.TypeKind == TypeKind.Class) returnTypes.Add(type1);
                if (type2?.TypeKind == TypeKind.Class) returnTypes.Add(type2);
            }
            // Handle block with multiple return statements
            else if (lambda.Body is BlockSyntax block)
            {
                var returns = block.DescendantNodes()
                    .OfType<ReturnStatementSyntax>()
                    .Select(r => model.GetTypeInfo(r.Expression).Type)
                    .OfType<INamedTypeSymbol>()
                    .Where(t => t.TypeKind == TypeKind.Class);

                returnTypes.AddRange(returns);
            }
            // Handle simple expression lambdas: () => new Foo()
            else
            {
                var type = model.GetTypeInfo(lambda.Body).Type as INamedTypeSymbol;
                if (type?.TypeKind == TypeKind.Class)
                    returnTypes.Add(type);
            }
        }
        else if (factoryArg is IdentifierNameSyntax methodRef)
        {
            var symbol = model.GetSymbolInfo(methodRef).Symbol;
            if (symbol is IMethodSymbol methodSymbol &&
                methodSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax method)
            {
                var returnStatements = method.DescendantNodes()
                    .OfType<ReturnStatementSyntax>()
                    .Select(r => model.GetTypeInfo(r.Expression).Type)
                    .OfType<INamedTypeSymbol>()
                    .Where(t => t.TypeKind == TypeKind.Class);

                returnTypes.AddRange(returnStatements);
            }
        }
        else
        {
            Console.WriteLine("UsingFactoryMethod passed non lambda or method");
            Console.WriteLine($"expression: {factoryArg.ToFullString()}");
        }

        return returnTypes.Distinct(new FullyQualifiedNameComparer());
    }
}