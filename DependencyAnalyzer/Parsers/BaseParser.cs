using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.Parsers;

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

    protected static IEnumerable<InvocationExpressionSyntax> FindInvocations(SyntaxNode root, SemanticModel model, string methodName, string fullyQualifiedDeclaringType)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) 
                        return false;
                if (memberAccess.Name.Identifier.Text != methodName) 
                        return false;

                var method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (method == null) return false;

                if (IsSameOrSubclassOf(method.ContainingType, fullyQualifiedDeclaringType))
                    return true;

                if (ImplementsInterface(method.ContainingType, fullyQualifiedDeclaringType))
                    return true;

                if (method.IsExtensionMethod)
                {
                    var reducedFrom = method.ReducedFrom;

                    var extendedType = reducedFrom?.Parameters.FirstOrDefault()?.Type;
                    if (extendedType != null)
                    {
                        if (IsSameOrSubclassOf(extendedType, fullyQualifiedDeclaringType) ||
                            ImplementsInterface(extendedType, fullyQualifiedDeclaringType))
                            return true;
                    }
                }

                return false;
            }).ToList();
    }

    protected static INamedTypeSymbol? GetCallingClassFromInvocation(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        ExpressionSyntax? receiverExpr = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            receiverExpr = memberAccess.Expression;
        }
        else if (invocation.Expression is IdentifierNameSyntax)
        {
            // Handle parameterless call like "Dispose()" in class scope
            receiverExpr = invocation.Expression;
        }

        if (receiverExpr != null)
        {
            var type = model.GetTypeInfo(receiverExpr).Type;

            if (type is INamedTypeSymbol namedType)
                return namedType;
        }

        Console.WriteLine("[WARN] Could not determine calling class for invocation:");
        Console.WriteLine($"\t{invocation.ToFullString()}");

        return null;
    }

    protected static IEnumerable<INamedTypeSymbol> FindImplementations(SemanticModel model, SyntaxNode root, string fullyQualifiedInterfaceName)
    {
        return root.DescendantNodes()
           .OfType<ClassDeclarationSyntax>()
           .Select(classDecl => model.GetDeclaredSymbol(classDecl))
           .OfType<INamedTypeSymbol>()
           .Where(symbol =>
               symbol is not null &&
               ImplementsInterface(symbol, fullyQualifiedInterfaceName)
            );
    }

    protected static bool IsSameOrSubclassOf(ITypeSymbol typeSymbol, string fullyQualifiedTypeName)
    {
        if (typeSymbol.ToDisplayString() == fullyQualifiedTypeName)
            return true;

        var current = typeSymbol.BaseType;

        while (current != null)
        {
            if (current.ToDisplayString() == fullyQualifiedTypeName)
                return true;

            current = current.BaseType;
        }

        return false;
    }

    protected static bool ImplementsInterface(ITypeSymbol typeSymbol, string fullyQualifiedInterfaceName)
    {
        return typeSymbol.AllInterfaces.Any(i => i.ToDisplayString() == fullyQualifiedInterfaceName);
    }

    //TODO add fullyQualifiedDeclaringType
    protected static InvocationExpressionSyntax? FindAncestorInvocationInChain(ExpressionSyntax expression,  string methodName)
    {
        SyntaxNode current = expression;

        while (current != null)
        {
            while (current is MemberAccessExpressionSyntax ma)
                current = ma.Parent;

            if (current is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Name.Identifier.Text == methodName)
                {
                    return invocation;
                }
            }

            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Only finds invocations on concrete classes / implementations
    /// </summary>
    protected static InvocationExpressionSyntax? FindDescendantInvocationInChain(ExpressionSyntax expression, SemanticModel model, string methodName, string fullyQualifiedDeclaringType)
    {
        SyntaxNode current = expression;

        while (true)
        {

            // Walk down to next invocation (skip pure property/member accesses)
            while (current is MemberAccessExpressionSyntax ma)
                current = ma.Expression;

            if (current is not InvocationExpressionSyntax invocation ||
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                break;
            }

            if (memberAccess.Name is GenericNameSyntax genericName &&
                genericName.Identifier.Text == methodName)
            {
                var symbol = model.GetSymbolInfo(memberAccess.Expression).Symbol;

                if (symbol is INamedTypeSymbol typeSymbol &&
                    IsSameOrSubclassOf(typeSymbol, fullyQualifiedDeclaringType))
                {
                    return invocation;
                }
            }

            current = memberAccess.Expression;
        }
        return null;
    }

    /// <summary>
    /// Finds type arguments in a method
    /// i.e. Method<T>() Returns the Type of what was passed for T
    /// </summary>
    protected static IEnumerable<INamedTypeSymbol> GetTypeArgumentsFromInvocation(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol == null)
            return Enumerable.Empty<INamedTypeSymbol>();

        return symbol.TypeArguments.OfType<INamedTypeSymbol>();
    }

    /// <summary>
    /// Finds arguments in a method that are of type System.Type.
    /// i.e. Method(string,Type,int) Returns the Type of what was passed as the second paramter.
    /// Method("test", typeof(String), 3) returns String
    /// </summary>
    protected static IEnumerable<INamedTypeSymbol> GetTypeArgumentsFromInvocationArguments(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var ret = new List<INamedTypeSymbol>();
        var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol == null)
            return Enumerable.Empty<INamedTypeSymbol>();

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var typeInfo = model.GetTypeInfo(arg.Expression);

            if (typeInfo.Type?.ToDisplayString() == "System.Type")
            {
                if (arg.Expression is TypeOfExpressionSyntax typeofExpr)
                {
                    var actualType = model.GetTypeInfo(typeofExpr.Type).Type as INamedTypeSymbol;
                    if (actualType != null)
                    {
                        ret.Add(actualType);
                    }
                }
                else
                {
                    Console.WriteLine($"[WARN] Could not resolve actual type of System.Type Argument:\n\t{invocation.ToFullString()}");
                }
            }
        }
       
        return ret;
    }
}