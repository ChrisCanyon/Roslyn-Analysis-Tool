using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

namespace DependencyAnalyzer.RegistrationParsers
{
    /// <summary>
    /// Parses and extracts registration information from Windsor container usage within a project.
    /// Focuses on identifying calls to <c>IWindsorContainer.Register(...)</c> and related installer usage.
    /// </summary>
    /// <remarks>
    /// Limitations:
    /// - Only detects direct <c>.Register(...)</c> calls on IWindsorContainer instances.
    /// - Installer analysis is limited to explicitly constructed installers passed to <c>.Install(...)</c>.
    /// - Does not currently analyze method bodies of installers or follow nested <c>.Install(...)</c> calls (i.e., no cascading installer resolution).
    /// - Does not infer runtime registration via factories, loops, or external assemblies.
    ///
    /// Intended for static analysis and graph generation, not for runtime fidelity.
    /// </remarks>
    public class WindsorRegistrationParser : IRegistrationParser
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

                // Get the semantic model for the current document (lets us resolve symbols)
                var model = await doc.GetSemanticModelAsync();

                if (root == null || model == null) continue;

                // Find all method invocation expressions in this file (e.g., container.Register(...))
                var invocations = FindWindsorRegisterInvocations(root, model);
                var currentInstallerSymbols = FindWindsorInstallerSymbol(model, root);

                foreach (var invocation in invocations)
                {
                    if (currentInstallerSymbols.Any())
                    {
                        registrations.AddRange(await CreateRegistrationsForInstaller(invocation, currentInstallerSymbols, solution, model, project.Name));
                    }

                    registrations.AddRange(ParseRegistration(invocation, model, project.Name));
                }
            }

            // Return all registrations found in this project
            return registrations;
        }

        /// <summary>
        /// Expands a Windsor registration into all projects that use the installer it was declared in.
        /// </summary>
        private async Task<IEnumerable<RegistrationInfo>> CreateRegistrationsForInstaller(
            InvocationExpressionSyntax invocation,
            IEnumerable<INamedTypeSymbol> installers,
            Solution solution,
            SemanticModel model, 
            string project)
        {
            var comparer = new FullyQualifiedNameComparer();
            var registration = ParseRegistration(invocation, model, project);

            var containingClass = invocation.Ancestors()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault();
            if (containingClass == null) return registration;

            var containingSymbol = model.GetDeclaredSymbol(containingClass) as INamedTypeSymbol;
            if (containingSymbol == null) return registration;

            //Get the installer this invocation is part of
            var installer = installers.FirstOrDefault(x => comparer.Equals(containingSymbol, x));
            if (installer == null) return registration; //this should never happen

            var calledProjects = await FindInstallerReferencesAsync(solution, installer);
            return calledProjects.SelectMany(project =>
                                            registration.Select(incompleteRegistration =>
                                            new RegistrationInfo
                                            {
                                                Interface = incompleteRegistration.Interface,
                                                Implementation = incompleteRegistration.Implementation,
                                                Lifetime = incompleteRegistration.Lifetime,
                                                ProjectName = project
                                            }
                                        ));
        }

        private static IEnumerable<InvocationExpressionSyntax> FindWindsorRegisterInvocations(SyntaxNode root, SemanticModel model)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation =>
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
                    if (memberAccess.Name.Identifier.Text != "Register") return false;

                    var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol == null) return false;

                    if (symbol.ContainingType.ToDisplayString() == "Castle.Windsor.IWindsorContainer")
                        return true;

                    // Wrapper/forwarder class: check implemented interfaces
                    return symbol.ContainingType.AllInterfaces
                        .Any(i => i.ToDisplayString() == "Castle.Windsor.IWindsorContainer");
                });
        }

        private static IEnumerable<INamedTypeSymbol> FindWindsorInstallerSymbol(SemanticModel model, SyntaxNode root)
        {
            return root.DescendantNodes()
               .OfType<ClassDeclarationSyntax>()
               .Select(classDecl => model.GetDeclaredSymbol(classDecl))
               .OfType<INamedTypeSymbol>()
               .Where(symbol =>
                   symbol is not null &&
                   symbol.AllInterfaces.Any(i => i.ToDisplayString() == "Castle.MicroKernel.Registration.IWindsorInstaller")
                );
        }

        private record InstallInvocationContext(
            InvocationExpressionSyntax Invocation,
            SemanticModel SemanticModel,
            string ProjectName
        );

        private IEnumerable<InstallInvocationContext> _windsorInstallContexts;
        private async Task<IEnumerable<InstallInvocationContext>> FindWindsorInstallInvocationContextsForSolution(Solution solution)
        {
            if(_windsorInstallContexts != null) return _windsorInstallContexts;
            var ret = new List<InstallInvocationContext>();

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync();
                    var model = await document.GetSemanticModelAsync();

                    if (root is null || model is null) continue;

                    ret.AddRange(root.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Where(invocation =>
                        {
                            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
                            if (memberAccess.Name.Identifier.Text != "Install") return false;

                            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                            if (symbol is null) return false;

                            if (symbol.ContainingType.ToDisplayString() == "Castle.Windsor.IWindsorContainer")
                                return true;

                            return symbol.ContainingType.AllInterfaces
                                .Any(i => i.ToDisplayString() == "Castle.Windsor.IWindsorContainer");
                        })
                        .Select(invocation => new InstallInvocationContext(invocation, model, project.Name))
                    );
                }
            }
            _windsorInstallContexts = ret;
            return _windsorInstallContexts;
        }

        private async Task<IEnumerable<string>> FindInstallerReferencesAsync(Solution solution, INamedTypeSymbol installerSymbol)
        {
            var result = new HashSet<string>();
            var comparer = new FullyQualifiedNameComparer();
            var installContexts = await FindWindsorInstallInvocationContextsForSolution(solution);

            foreach (var installContext in installContexts)
            {
                var model = installContext.SemanticModel;
                var invocation = installContext.Invocation;
                var project = installContext.ProjectName;

                var argSymbols = invocation.ArgumentList.Arguments
                    .Select(arg => model.GetTypeInfo(arg.Expression).Type)
                    .OfType<INamedTypeSymbol>();

                if (argSymbols.Any(arg => comparer.Equals(arg, installerSymbol)))
                {
                    result.Add(project);
                }
            }

            return result;
        }

        private struct ImplementationStrategy
        {
            public ImplementationStrategyType ImplStratType;
            public IEnumerable<INamedTypeSymbol> ImplReturnTypes;
        }

        private enum ImplementationStrategyType
        {
            ImplementedBy,
            Factory,
            None
        }

        private static IEnumerable<INamedTypeSymbol> GetFactoryReturnTypes(ExpressionSyntax factoryArg, SemanticModel model)
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

        private static ImplementationStrategy GetImplementedByStrat(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            MemberAccessExpressionSyntax? memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
            if (!(memberAccess?.Name is GenericNameSyntax genericImpl) ||
                genericImpl.TypeArgumentList.Arguments.Count != 1)
            {
                return new ImplementationStrategy
                {
                    ImplReturnTypes = [],
                    ImplStratType = ImplementationStrategyType.None
                };
            }

            var implType = model.GetSymbolInfo(genericImpl.TypeArgumentList.Arguments[0]).Symbol as INamedTypeSymbol;
            if (implType != null)
            {
                return new ImplementationStrategy
                {
                    ImplReturnTypes = [implType],
                    ImplStratType = ImplementationStrategyType.ImplementedBy
                };
            }
            else
            {
                Console.WriteLine("could not determine implemented by return types");
                Console.WriteLine($"expression: {invocation.ToFullString()}");
                return new ImplementationStrategy
                {
                    ImplReturnTypes = [],
                    ImplStratType = ImplementationStrategyType.Factory
                };
            }
        }

        private static ImplementationStrategy GetUsingFactoryMethodStrat(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            var factoryArg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (factoryArg != null)
            {
                IEnumerable<INamedTypeSymbol> factoryReturn = GetFactoryReturnTypes(factoryArg, model);

                if (factoryReturn.Any())
                {
                    return new ImplementationStrategy
                    {
                        ImplReturnTypes = factoryReturn,
                        ImplStratType = ImplementationStrategyType.Factory
                    };
                }
                else
                {
                    Console.WriteLine("could not determine factory return types");
                    Console.WriteLine($"expression: {invocation.ToFullString()}");
                    return new ImplementationStrategy
                    {
                        ImplReturnTypes = [],
                        ImplStratType = ImplementationStrategyType.Factory
                    };
                }
            }

            return new ImplementationStrategy
            {
                ImplReturnTypes = [],
                ImplStratType = ImplementationStrategyType.None
            };
        }


        private ImplementationStrategy GetImplementationStrategy(ExpressionSyntax expression, SemanticModel model)
        {
            InvocationExpressionSyntax? invocation;

            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "ImplementedBy");
            if (invocation != null) {

                return GetImplementedByStrat(invocation, model);
            }

            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "UsingFactoryMethod");
            if(invocation != null)
            {
                return GetUsingFactoryMethodStrat(invocation, model);
            }
            
            return new ImplementationStrategy
            {
                ImplReturnTypes = [],
                ImplStratType = ImplementationStrategyType.None
            };
        }

        private IEnumerable<RegistrationInfo> CreateRegistrationsFromComponentFor(ExpressionSyntax expression, GenericNameSyntax methodName, SemanticModel model, string projectName)
        {
            var lifestyle = ExtractLifestyleFromChain(expression);
            var ret = new List<RegistrationInfo>();
            //go through each type argument
            foreach (var arg in methodName.TypeArgumentList.Arguments)
            {
                var symbol = model.GetSymbolInfo(arg).Symbol as INamedTypeSymbol;

                if (symbol == null)
                {
                    Console.WriteLine("Non NamedTypeSymbol found in Component.For TypeArgumentList");
                    Console.WriteLine($"expression: {expression.ToFullString()}");
                    continue;
                }

                switch (symbol.TypeKind)
                {
                    case TypeKind.Interface:
                        ret.AddRange(GetRegistrationForInterface(model, expression, symbol, lifestyle, projectName));
                        break;
                    case TypeKind.Class:
                        ret.Add(GetRegistrationForConcreteClass(model, expression, symbol, lifestyle, projectName));
                        break;
                    default:
                        Console.WriteLine("Found non class/interface argument for Component.For TypeArgumentList");
                        Console.WriteLine($"expression: {expression.ToFullString()}");
                        break;
                }
            }

            return ret;
        }

        /// <summary>
        /// Parses a Windsor `Register` invocation to extract registration information such as:
        /// interface, implementation, and lifestyle.
        ///
        /// Currently supports:
        /// - Explicit registrations using `Component.For<T>().ImplementedBy<U>()`
        /// - Factory method registrations via `.UsingFactoryMethod(...)`
        /// - Instance registrations via `.Instance(...)`
        ///
        /// Does **not** support:
        /// - Convention-based registrations (e.g., `Classes.FromThisAssembly()`)
        /// - Configuration-based registrations (e.g., `FromXmlFile(...)`)
        /// </summary>
        private IEnumerable<RegistrationInfo> ParseRegistration(InvocationExpressionSyntax registerInvocation, SemanticModel model, string projectName)
        {
            var ret = new List<RegistrationInfo>();

            foreach (var registrationArg in registerInvocation.ArgumentList.Arguments)
            {
                var argExpression = registrationArg.Expression;

                // Walk down the member access chain to find the For<T> call
                while (argExpression is InvocationExpressionSyntax methodCall &&
                        methodCall.Expression is MemberAccessExpressionSyntax memberAccessExpr)
                {
                    if (memberAccessExpr.Name is GenericNameSyntax methodName &&
                        methodName.Identifier.Text == "For" &&
                        methodName.TypeArgumentList.Arguments.Count > 0)
                    {
                        // Make sure it's Castle Windsor's Component.For<T>()
                        var symbol = model.GetSymbolInfo(memberAccessExpr.Expression).Symbol;

                        if (symbol is INamedTypeSymbol typeSymbol &&
                            typeSymbol.ToDisplayString() == "Castle.MicroKernel.Registration.Component")
                        {
                            ret.AddRange(CreateRegistrationsFromComponentFor(argExpression, methodName, model, projectName));
                        }
                    }
                    argExpression = memberAccessExpr.Expression;
                }
            }
            return ret;
        }
                
        private List<RegistrationInfo> GetRegistrationForInterface(SemanticModel model, ExpressionSyntax expression, INamedTypeSymbol registeredInterface, LifetimeTypes regType, string projectName)
        {
            var implStrat = GetImplementationStrategy(expression, model);
            var ret = new List<RegistrationInfo>();

            if (implStrat.ImplStratType != ImplementationStrategyType.ImplementedBy &&
                implStrat.ImplStratType != ImplementationStrategyType.Factory)
            {
                Console.WriteLine("No implementation strat found for interface");
                Console.WriteLine($"expression {expression.ToFullString()}");
                return ret;
            }

            if (!implStrat.ImplReturnTypes.Any())
            {
                ret.Add(new RegistrationInfo
                {
                    Interface = registeredInterface,
                    Implementation = null,
                    Lifetime = regType,
                    ProjectName = projectName,
                    UnresolvableImplementation = true,
                    IsFactoryResolved = implStrat.ImplStratType == ImplementationStrategyType.Factory
                });
                return ret;
            }

            foreach (var impType in implStrat.ImplReturnTypes)
            {
                if(impType.TypeKind == TypeKind.Interface)
                {
                    Console.WriteLine("found interface implementing interface");
                    Console.WriteLine($"expression {expression.ToFullString()}");
                    continue;
                }
                ret.Add(new RegistrationInfo
                {
                    Interface = registeredInterface,
                    Implementation = impType,
                    Lifetime = regType,
                    ProjectName = projectName,
                    IsFactoryResolved = implStrat.ImplStratType == ImplementationStrategyType.Factory
                });
            }

            return ret;
        }

        //todo maybe care about if this is implemented by something for some ungodly reason
        private RegistrationInfo GetRegistrationForConcreteClass(SemanticModel model, ExpressionSyntax expression, INamedTypeSymbol registeredClass, LifetimeTypes regType, string projectName)
        {
            var implStrat = GetImplementationStrategy(expression, model);
            return new RegistrationInfo
            {
                Interface = null,
                Implementation = registeredClass,
                Lifetime = regType,
                ProjectName = projectName,
                IsFactoryResolved = implStrat.ImplStratType == ImplementationStrategyType.Factory
            };
        }

        private LifetimeTypes ExtractLifestyleFromChain(ExpressionSyntax expression)
        {
            InvocationExpressionSyntax? invocation;
            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "LifestyleTransient");
            if (invocation != null) return LifetimeTypes.Transient;
            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "LifestyleSingleton");
            if (invocation != null) return LifetimeTypes.Singleton;
            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "LifestyleScoped");
            if (invocation != null) return LifetimeTypes.PerWebRequest;
            invocation = ExpressionTraversalHelper.FindAncestorInvocationInChain(expression, "LifestylePerWebRequest");
            if (invocation != null) return LifetimeTypes.PerWebRequest;

            return LifetimeTypes.Singleton; // 🟢 Default if no lifestyle found
        }
    }
}
