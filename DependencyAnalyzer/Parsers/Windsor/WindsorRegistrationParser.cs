using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq.Expressions;

namespace DependencyAnalyzer.Parsers.Windsor
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
    public class WindsorRegistrationParser : BaseParser, IRegistrationParser
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

                var invocations = FindInvocations(root, model, "Register", "Castle.Windsor.IWindsorContainer");
                var installerSymbols = FindImplementations(model, root, "Castle.MicroKernel.Registration.IWindsorInstaller");

                foreach (var invocation in invocations)
                {
                    if (installerSymbols.Any())
                    {
                        registrations.AddRange(await CreateRegistrationsForInstaller(invocation, installerSymbols, solution, model, project.Name));
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

        private record InstallInvocationContext(
            InvocationExpressionSyntax Invocation,
            SemanticModel SemanticModel,
            string ProjectName
        );

        private IEnumerable<InstallInvocationContext> _windsorInstallContexts;
        private async Task<IEnumerable<InstallInvocationContext>> FindWindsorInstallInvocationContextsForSolution(Solution solution)
        {
            if (_windsorInstallContexts != null) return _windsorInstallContexts;
            var ret = new List<InstallInvocationContext>();

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync();
                    var model = await document.GetSemanticModelAsync();

                    if (root is null || model is null) continue;

                    IEnumerable<InvocationExpressionSyntax> installInvocations = FindInvocations(root, model, "Install", "Castle.Windsor.IWindsorContainer");

                    ret.AddRange(installInvocations.Select(invocation =>
                                    new InstallInvocationContext(invocation, model, project.Name))
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


        private ImplementationStrategy GetImplementationStrategy(ExpressionSyntax componentForExpression, SemanticModel model)
        {
            InvocationExpressionSyntax? invocation;

            invocation = FindAncestorInvocationInChain(componentForExpression, "ImplementedBy");
            if (invocation != null)
            {

                return GetImplementedByStrat(invocation, model);
            }

            invocation = FindAncestorInvocationInChain(componentForExpression, "UsingFactoryMethod");
            if (invocation != null)
            {
                return GetUsingFactoryMethodStrat(invocation, model);
            }

            return new ImplementationStrategy
            {
                ImplReturnTypes = [],
                ImplStratType = ImplementationStrategyType.None
            };
        }

        private IEnumerable<RegistrationInfo> CreateRegistrationsFromComponentFor(ExpressionSyntax componentForExpression, GenericNameSyntax methodName, SemanticModel model, string projectName)
        {
            var lifestyle = ExtractLifestyleFromChain(componentForExpression);
            var ret = new List<RegistrationInfo>();
            //go through each type argument
            foreach (var arg in methodName.TypeArgumentList.Arguments)
            {
                var symbol = model.GetSymbolInfo(arg).Symbol as INamedTypeSymbol;

                if (symbol == null)
                {
                    Console.WriteLine("Non NamedTypeSymbol found in Component.For TypeArgumentList");
                    Console.WriteLine($"expression: {componentForExpression.ToFullString()}");
                    continue;
                }

                switch (symbol.TypeKind)
                {
                    case TypeKind.Interface:
                        ret.AddRange(GetRegistrationForInterface(model, componentForExpression, symbol, lifestyle, projectName));
                        break;
                    case TypeKind.Class:
                        ret.Add(GetRegistrationForConcreteClass(model, componentForExpression, symbol, lifestyle, projectName));
                        break;
                    default:
                        Console.WriteLine("Found non class/interface argument for Component.For TypeArgumentList");
                        Console.WriteLine($"expression: {componentForExpression.ToFullString()}");
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
                var invocation = FindDescendantInvocationInChain(registrationArg.Expression, model, "For", "Castle.MicroKernel.Registration.Component");
                if (invocation == null) continue;

                if (invocation is not null &&
                        invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax methodName } &&
                        methodName.TypeArgumentList.Arguments.Count > 0)
                {
                    ret.AddRange(CreateRegistrationsFromComponentFor(invocation, methodName, model, projectName));
                }
            }
            return ret;
        }

        private List<RegistrationInfo> GetRegistrationForInterface(SemanticModel model, ExpressionSyntax componentForExpression, INamedTypeSymbol registeredInterface, LifetimeTypes regType, string projectName)
        {
            var implStrat = GetImplementationStrategy(componentForExpression, model);
            var ret = new List<RegistrationInfo>();

            if (implStrat.ImplStratType != ImplementationStrategyType.ImplementedBy &&
                implStrat.ImplStratType != ImplementationStrategyType.Factory)
            {
                Console.WriteLine("No implementation strat found for interface");
                Console.WriteLine($"expression {componentForExpression.ToFullString()}");
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
                if (impType.TypeKind == TypeKind.Interface)
                {
                    Console.WriteLine("found interface implementing interface");
                    Console.WriteLine($"expression {componentForExpression.ToFullString()}");
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
        private RegistrationInfo GetRegistrationForConcreteClass(SemanticModel model, ExpressionSyntax componentForExpression, INamedTypeSymbol registeredClass, LifetimeTypes regType, string projectName)
        {
            var implStrat = GetImplementationStrategy(componentForExpression, model);
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
            invocation = FindAncestorInvocationInChain(expression, "LifestyleTransient");
            if (invocation != null) return LifetimeTypes.Transient;
            invocation = FindAncestorInvocationInChain(expression, "LifestyleSingleton");
            if (invocation != null) return LifetimeTypes.Singleton;
            invocation = FindAncestorInvocationInChain(expression, "LifestyleScoped");
            if (invocation != null) return LifetimeTypes.PerWebRequest;
            invocation = FindAncestorInvocationInChain(expression, "LifestylePerWebRequest");
            if (invocation != null) return LifetimeTypes.PerWebRequest;

            return LifetimeTypes.Singleton; // 🟢 Default if no lifestyle found
        }
    }
}
