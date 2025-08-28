﻿using DependencyAnalyzer.Interfaces;
using DependencyAnalyzer.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;

namespace DependencyAnalyzer.Parsers.MicrosoftDI;

//TODO REWRITE THIS IS REALLY SLOW FOR SOME REASON
public class MicrosoftDIRegistrationParser : BaseParser, IRegistrationParser
{
    public async Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution)
    {
        Console.WriteLine($"Info: Parsing Microsoft DI Registration Info");
        var stopwatch = Stopwatch.StartNew();

        var registrations = new List<RegistrationInfo>();
        var registrationTasks = new List<Task<List<RegistrationInfo>>>();

        foreach (var project in solution.Projects)
        {
            if (project.Name.ToLower().Contains("test"))
                continue;

            registrationTasks.Add(GetRegistrationsFromProjectAsync(project));
        }

        await Task.WhenAll(registrationTasks);
        foreach (var task in registrationTasks)
            registrations.AddRange(task.Result);

        stopwatch.Stop();
        Console.WriteLine($"~~~ M.DI Registration Parse ~~~");
        Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
        return registrations;
    }

    private async Task<List<RegistrationInfo>> GetRegistrationsFromProjectAsync(Project project)
    {
        var registrations = new List<RegistrationInfo>();

        foreach (var doc in project.Documents)
        {
            var rootTask = doc.GetSyntaxRootAsync();
            var modelTask = doc.GetSemanticModelAsync();

            await Task.WhenAll(rootTask, modelTask);

            var root = rootTask.Result;
            var model = modelTask.Result;
            if (root == null || model == null) continue;

            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol == null) continue;

                var methodName = symbol.Name;
                var containingType = symbol.ContainingType.ToDisplayString();
                var args = invocation.ArgumentList?.Arguments;

                // AddDbContext<TContext>()
                if (methodName == "AddDbContext" &&
                    symbol.IsGenericMethod &&
                    symbol.TypeArguments.Length == 1 &&
                    (containingType == "Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions" ||
                     containingType == "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions"))
                {
                    if (symbol.TypeArguments[0] is INamedTypeSymbol dbContextSymbol)
                    {
                        registrations.Add(new RegistrationInfo
                        {
                            ServiceType = null,
                            ImplementationType = dbContextSymbol,
                            ProjectName = project.Name,
                            Lifetime = LifetimeTypes.PerWebRequest,
                            IsFactoryResolved = true
                        });
                    }
                    continue;
                }

                // AddX<TInterface, TImpl>(MethodGroup)
                if (methodName is "AddScoped" or "AddTransient" or "AddSingleton" &&
                    symbol.IsGenericMethod &&
                    symbol.TypeArguments.Length == 2 &&
                    args?.Count == 1 &&
                    args?[0].Expression is IdentifierNameSyntax methodRef)
                {
                    var interfaceType = symbol.TypeArguments[0] as INamedTypeSymbol;
                    var factoryReturnTypes = GetFactoryReturnTypes(methodRef, model);

                    foreach (var returnType in factoryReturnTypes)
                    {
                        registrations.Add(new RegistrationInfo
                        {
                            ServiceType = interfaceType,
                            ImplementationType = returnType,
                            ProjectName = project.Name,
                            Lifetime = GetLifetime(methodName),
                            IsFactoryResolved = true
                        });
                    }

                    continue;
                }

                // AddX<TInterface, TImpl>() or AddX<TImpl>()
                if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName })
                {
                    var typeArgs = genericName.TypeArgumentList.Arguments;
                    if (typeArgs.Count >= 1 && methodName is "AddScoped" or "AddTransient" or "AddSingleton")
                    {
                        var lifetime = GetLifetime(methodName);
                        var interfaceType = model.GetTypeInfo(typeArgs[0]).Type as INamedTypeSymbol;
                        var implementationType = typeArgs.Count > 1
                            ? model.GetTypeInfo(typeArgs[1]).Type as INamedTypeSymbol
                            : interfaceType;

                        registrations.Add(new RegistrationInfo
                        {
                            ServiceType = interfaceType,
                            ImplementationType = implementationType,
                            ProjectName = project.Name,
                            Lifetime = lifetime,
                            IsFactoryResolved = false
                        });

                        continue;
                    }
                }
            }

            // Parse service registrations inside methods with IServiceCollection
            var serviceMethods = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.ParameterList.Parameters.Any(p =>
                    model.GetTypeInfo(p.Type).Type?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection"));

            foreach (var method in serviceMethods)
                ExtractServiceRegistrationsFromMethod(method, model, project, registrations);
        }

        return registrations;
    }

    private void ExtractServiceRegistrationsFromMethod(
        MethodDeclarationSyntax method,
        SemanticModel model,
        Project project,
        List<RegistrationInfo> registrations)
    {
        var serviceParams = method.ParameterList.Parameters
            .Where(p => model.GetTypeInfo(p.Type).Type?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection")
            .Select(p => p.Identifier.Text)
            .ToHashSet();

        if (!serviceParams.Any()) return;

        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
                memberAccess.Expression is not IdentifierNameSyntax identifier ||
                !serviceParams.Contains(identifier.Identifier.Text))
                continue;

            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol?.ContainingType == null) continue;

            var methodName = symbol.Name;
            var containingType = symbol.ContainingType.ToDisplayString();
            var args = invocation.ArgumentList?.Arguments;

            if (!containingType.Contains("DependencyInjection")) continue;

            // AddHttpClient<TInterface, TImpl>
            TryAddHttpClient(project, memberAccess.Name, model, registrations);

            // AddX(() => new Impl())
            if (methodName is "AddScoped" or "AddTransient" or "AddSingleton" &&
                args?.Count == 1 &&
                args?[0].Expression is LambdaExpressionSyntax lambda)
            {
                var factories = GetFactoryReturnTypes(lambda, model);
                foreach (var factory in factories)
                {
                    registrations.Add(new RegistrationInfo
                    {
                        ServiceType = null,
                        ImplementationType = factory,
                        ProjectName = project.Name,
                        Lifetime = GetLifetime(methodName),
                        IsFactoryResolved = true
                    });
                }

                continue;
            }

            // AddDbContext<T>()
            if (methodName == "AddDbContext" &&
                symbol.IsGenericMethod &&
                symbol.TypeArguments.Length == 1 &&
                symbol.TypeArguments[0] is INamedTypeSymbol dbContextType)
            {
                registrations.Add(new RegistrationInfo
                {
                    ServiceType = null,
                    ImplementationType = dbContextType,
                    ProjectName = project.Name,
                    Lifetime = LifetimeTypes.PerWebRequest,
                    IsFactoryResolved = true
                });

                continue;
            }

            // AddX<TImpl> or AddX<TInterface, TImpl>
            var lifetime = GetLifetime(methodName);
            if (memberAccess.Name is not GenericNameSyntax generic || generic.TypeArgumentList.Arguments.Count == 0)
                continue;

            var typeArgs = generic.TypeArgumentList.Arguments;
            INamedTypeSymbol? iface = null;
            INamedTypeSymbol? impl = null;

            if (typeArgs.Count == 1)
            {
                impl = model.GetTypeInfo(typeArgs[0]).Type as INamedTypeSymbol;
            }
            else if (typeArgs.Count == 2)
            {
                iface = model.GetTypeInfo(typeArgs[0]).Type as INamedTypeSymbol;
                impl = model.GetTypeInfo(typeArgs[1]).Type as INamedTypeSymbol;
            }

            if (impl != null)
            {
                registrations.Add(new RegistrationInfo
                {
                    ServiceType = iface,
                    ImplementationType = impl,
                    ProjectName = project.Name,
                    Lifetime = lifetime
                });
            }
        }
    }

    private static void TryAddHttpClient(Project project, ExpressionSyntax expression, SemanticModel model, List<RegistrationInfo> registrations)
    {
        if (expression is not GenericNameSyntax generic || generic.Identifier.Text != "AddHttpClient") return;

        var symbol = model.GetSymbolInfo(generic).Symbol as IMethodSymbol;
        if (symbol?.ContainingNamespace.ToDisplayString().Contains("Microsoft.Extensions.DependencyInjection") != true)
            return;

        if (generic.TypeArgumentList.Arguments.Count != 2)
            return;

        var interfaceSymbol = model.GetSymbolInfo(generic.TypeArgumentList.Arguments[0]).Symbol as INamedTypeSymbol;
        var implementationSymbol = model.GetSymbolInfo(generic.TypeArgumentList.Arguments[1]).Symbol as INamedTypeSymbol;

        if (interfaceSymbol == null || implementationSymbol == null) return;

        registrations.Add(new RegistrationInfo
        {
            ServiceType = interfaceSymbol,
            ImplementationType = implementationSymbol,
            ProjectName = project.Name,
            Lifetime = LifetimeTypes.Transient,
            IsFactoryResolved = true
        });
    }

    private static LifetimeTypes GetLifetime(string methodName) => methodName switch
    {
        "AddSingleton" => LifetimeTypes.Singleton,
        "AddScoped" => LifetimeTypes.PerWebRequest,
        "AddTransient" => LifetimeTypes.Transient,
        _ => LifetimeTypes.Singleton
    };
}
