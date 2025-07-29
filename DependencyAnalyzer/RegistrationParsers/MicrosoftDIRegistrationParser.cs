using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.RegistrationParsers
{
    public class MicrosoftDIRegistrationParser : IRegistrationParser
    {
        public async Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution)
        {
            var ret = new List<RegistrationInfo>();

            var registrationTasks = new List<Task<List<RegistrationInfo>>>();

            foreach (var project in solution.Projects)
            {
                if (project.Name.ToLower().Contains("test")) continue;
                //no async for debug
#if DEBUG
                var projectRegistrations = await GetRegistrationsFromProjectAsync(project, solution);
                ret.AddRange(projectRegistrations);
#else
                registrationTasks.Add(GetRegistrationsFromProjectAsync(project, solution));
#endif
            }

            await Task.WhenAll(registrationTasks.ToArray());

            foreach (var task in registrationTasks)
            {
                var projectRegistrations = task.Result;
                ret.AddRange(projectRegistrations);
            }

            return ret;
        }

        private async Task<List<RegistrationInfo>> GetRegistrationsFromProjectAsync(Project project, Solution solution)
        {
            var registrations = new List<RegistrationInfo>();

            foreach (var doc in project.Documents)
            {
                var root = await doc.GetSyntaxRootAsync();
                var model = await doc.GetSemanticModelAsync();
                if (root == null || model == null) continue;

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
         
                foreach (var invocation in invocations)
                {
                    var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol?.ContainingType == null) continue;
         
                    var containingType = symbol.ContainingType.ToDisplayString();
                    var methodName = symbol.Name;
         
                    if (containingType != "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions")
                        continue;
                    
                    var expression = invocation.Expression;

                    GetHttpClientsAndDbContext(project, expression, model, registrations);

                    LifetimeTypes? lifetime = methodName switch
                    {
                        "AddSingleton" => LifetimeTypes.Singleton,
                        "AddScoped" => LifetimeTypes.PerWebRequest,
                        "AddTransient" => LifetimeTypes.Transient,
                        _ => null
                    };

                    if (lifetime == null) continue;

                    INamedTypeSymbol? interfaceType = null;
                    INamedTypeSymbol? implementationType = null;

                    // Look for generic type arguments
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                        memberAccess.Name is GenericNameSyntax genericName &&
                        genericName.TypeArgumentList.Arguments.Count > 0)
                    {
                        var typeArgs = genericName.TypeArgumentList.Arguments;

                        if (typeArgs.Count == 1)
                        {
                            implementationType = model.GetTypeInfo(typeArgs[0]).Type as INamedTypeSymbol;
                        }
                        else if (typeArgs.Count == 2)
                        {
                            interfaceType = model.GetTypeInfo(typeArgs[0]).Type as INamedTypeSymbol;
                            implementationType = model.GetTypeInfo(typeArgs[1]).Type as INamedTypeSymbol;
                        }
                    }

                    registrations.Add(new RegistrationInfo
                    {
                        Interface = interfaceType,
                        Implementation = implementationType,
                        ProjectName = project.Name,
                        Lifetime = lifetime.Value
                    });
                }
            }

            return registrations;
        }

        private static void GetHttpClientsAndDbContext(Project project, ExpressionSyntax expression, SemanticModel model,
            List<RegistrationInfo> registrations)
        {
            // Handle AddHttpClient<IMyInterface, MyImplementation>
            if (expression is GenericNameSyntax genericName && genericName.Identifier.Text == "AddHttpClient")
            {
                var symbolInfo = model.GetSymbolInfo(genericName);
                var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
 
                if (methodSymbol?.ContainingNamespace.ToDisplayString().Contains("Microsoft.Extensions.DependencyInjection") == true &&
                    genericName.TypeArgumentList.Arguments.Count == 2)
                {
                    var interfaceSymbol = model.GetSymbolInfo(genericName.TypeArgumentList.Arguments[0]).Symbol as INamedTypeSymbol;
                    var implementationSymbol = model.GetSymbolInfo(genericName.TypeArgumentList.Arguments[1]).Symbol as INamedTypeSymbol;
 
                    registrations.Add(new RegistrationInfo
                    {
                        Interface = interfaceSymbol,
                        Implementation = implementationSymbol,
                        ProjectName = project.Name,
                        Lifetime = LifetimeTypes.Transient,
                        IsFactoryResolved = true
                    });
                }
            }
 
            // Handle AddDbContext<MyDbContext>()
            if (expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "AddDbContext")
            {
                if (memberAccess.Expression is IdentifierNameSyntax || memberAccess.Expression is MemberAccessExpressionSyntax)
                {
                    var symbolInfo = model.GetSymbolInfo(memberAccess);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
 
                    if (methodSymbol?.ContainingNamespace.ToDisplayString().Contains("Microsoft.Extensions.DependencyInjection") == true &&
                        methodSymbol.IsGenericMethod && methodSymbol.TypeArguments.Length == 1)
                    {
                        var dbContextType = methodSymbol.TypeArguments[0] as INamedTypeSymbol;
 
                        registrations.Add(new RegistrationInfo
                        {
                            Interface = null,
                            Implementation = dbContextType,
                            ProjectName = project.Name,
                            Lifetime = LifetimeTypes.PerWebRequest,
                            IsFactoryResolved = true
                        });
                    }
                }
            }
        }
    }
}
