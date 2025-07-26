using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    class WindsorRegistrationParser : IRegistrationParser
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
                IEnumerable<string> calledProjects = [];
                if (currentInstallerSymbols.Count() > 0) calledProjects = await FindInstallerReferencesAsync(solution, currentInstallerSymbols);

                foreach (var invocation in invocations)
                {
                    // Try to parse this invocation into a structured RegistrationInfo object
                    var registration = ParseRegistration(invocation, model, project.Name);

                    //Clean up installer junk
                    if (currentInstallerSymbols.Any() && calledProjects.Any())
                    {
                        registration = registration.SelectMany(incompleteRegistration =>
                                                        calledProjects.Select(project =>
                                                        new RegistrationInfo
                                                        {
                                                            Interface = incompleteRegistration.Interface,
                                                            Implementation = incompleteRegistration.Implementation,
                                                            RegistrationType = incompleteRegistration.RegistrationType,
                                                            ProjectName = project
                                                        })).ToList();
                    }

                    registrations.AddRange(registration);
                }
            }

            // Return all registrations found in this project
            return registrations;
        }

        private IEnumerable<InvocationExpressionSyntax> FindWindsorRegisterInvocations(SyntaxNode root, SemanticModel model)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation =>
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
                    if (memberAccess.Name.Identifier.Text != "Register") return false;

                    var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol == null) return false;

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
                   symbol.AllInterfaces.Any(i => i.ToDisplayString() == "Castle.Windsor.Installer.IWindsorInstaller"));
        }
        
        //TODO cache results
        private static IEnumerable<InvocationExpressionSyntax> FindWindsorInstallInvocations(SyntaxNode root, SemanticModel model)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(invocation =>
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
                    if (memberAccess.Name.Identifier.Text != "Install") return false;

                    var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol is null) return false;

                    return symbol.ContainingType.AllInterfaces
                        .Any(i => i.ToDisplayString() == "Castle.Windsor.IWindsorContainer");
                });
        }

        private async Task<IEnumerable<string>> FindInstallerReferencesAsync(Solution solution, IEnumerable<INamedTypeSymbol> installerSymbols)
        {
            var result = new HashSet<string>();
            var comparer = SymbolEqualityComparer.Default;

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync();
                    var model = await document.GetSemanticModelAsync();
                    if (root is null || model is null) continue;

                    var installInvocations = FindWindsorInstallInvocations(root, model);

                    foreach (var installInvocation in installInvocations)
                    {
                        var argSymbols = installInvocation.ArgumentList.Arguments
                            .Select(arg => model.GetTypeInfo(arg.Expression).Type)
                            .OfType<INamedTypeSymbol>();

                        if (argSymbols.Any(arg => installerSymbols.Any(installer => comparer.Equals(arg, installer))))
                        {
                            result.Add(project.Name);
                            break;
                        }
                    }
                }
            }

            return result;
        }

        private List<RegistrationInfo> ParseRegistration(InvocationExpressionSyntax invocation, SemanticModel model, string projectName)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var fullCall = invocation.ToFullString();
                // Try to resolve the type arguments in For<>() and ImplementedBy<>()
                var genericTypes = invocation.DescendantNodes().OfType<GenericNameSyntax>();

                var registeredSymbols = genericTypes.SelectMany(g =>
                    g.TypeArgumentList.Arguments.Select(arg => model.GetSymbolInfo(arg).Symbol as INamedTypeSymbol))
                    .Where(s => s != null)
                    .Cast<INamedTypeSymbol>()
                    .ToList();

                if (registeredSymbols.Count == 0)
                {
                    Console.WriteLine($"Unable to find any symbols");
                    Console.WriteLine($"~~~ START FULL CALL ~~~~");
                    Console.WriteLine($"{invocation.ToFullString()}");
                    Console.WriteLine($"~~~ END FULL CALL ~~~~");
                    return new List<RegistrationInfo>();
                }

                if (fullCall.Contains("UsingFactoryMethod"))
                {
                    return new List<RegistrationInfo>() { GetRegistrationForFactoryMethod(registeredSymbols.First(), invocation, projectName) };
                }

                if (registeredSymbols.Count == 1)
                {
                    return new List<RegistrationInfo>() { GetRegistrationForImplementationOnly(registeredSymbols.First(), invocation, projectName) };
                }
                else if (registeredSymbols.Count > 2)
                {

                    var componentForCall = invocation
                        .DescendantNodes()
                        .OfType<GenericNameSyntax>()
                        .FirstOrDefault(g =>
                            g.Identifier.Text == "For" &&
                            g.TypeArgumentList.Arguments.Count > 1);

                    if (componentForCall != null)
                    {
                        var ret = new List<RegistrationInfo>
                        {
                            GetRegistrationForImplementationAndInterface(registeredSymbols[0], registeredSymbols[2], invocation, projectName),
                            GetRegistrationForImplementationAndInterface(registeredSymbols[1], registeredSymbols[2], invocation, projectName)
                        };
                        return ret;
                    }
                }
                else
                {
                    return new List<RegistrationInfo>() { GetRegistrationForImplementationAndInterface(registeredSymbols[0], registeredSymbols[1], invocation, projectName) };
                }
            }

            Console.WriteLine($"Couldnt map call at all");
            Console.WriteLine($"~~~ START FULL CALL ~~~~");
            Console.WriteLine($"{invocation.ToFullString()}");
            Console.WriteLine($"~~~ END FULL CALL ~~~~");

            return new List<RegistrationInfo>();
        }

        private RegistrationInfo GetRegistrationForFactoryMethod(INamedTypeSymbol registeredSymbol, InvocationExpressionSyntax invocation, string projectName)
        {
            var regType = ExtractLifestyle(invocation);

            if (registeredSymbol.TypeKind == TypeKind.Interface)
            {
                return new RegistrationInfo
                {
                    Interface = registeredSymbol,
                    Implementation = null,
                    RegistrationType = regType,
                    ProjectName = projectName,
                    IsFactoryMethod = true
                };
            }
            else
            {
                return new RegistrationInfo
                {
                    Interface = null,
                    Implementation = registeredSymbol,
                    RegistrationType = regType,
                    ProjectName = projectName,
                    IsFactoryMethod = false //factory methods that dont implement interfaces must return the type specified
                };
            }
        }

        private RegistrationInfo GetRegistrationForImplementationOnly(INamedTypeSymbol registeredClass, InvocationExpressionSyntax invocation, string projectName)
        {
            if (registeredClass.TypeKind == TypeKind.Interface)
            {
                Console.WriteLine("GetRegistrationForImplementationOnly passed an interface");
                Console.WriteLine($"~~~ START FULL CALL ~~~~");
                Console.WriteLine($"{invocation.ToFullString()}");
                Console.WriteLine($"~~~ END FULL CALL ~~~~");
            }

            var regType = ExtractLifestyle(invocation);

            return new RegistrationInfo
            {
                Interface = null,
                Implementation = registeredClass,
                RegistrationType = regType,
                ProjectName = projectName
            };
        }
        private RegistrationInfo GetRegistrationForImplementationAndInterface(INamedTypeSymbol registeredInterface, INamedTypeSymbol registeredImplementation, InvocationExpressionSyntax invocation, string projectName)
        {
            var regType = ExtractLifestyle(invocation);


            if (registeredImplementation.TypeKind == TypeKind.Interface)
            {
                Console.WriteLine("GetRegistrationForImplementationAndInterface passed an interface as implementation");
                Console.WriteLine($"~~~ START FULL CALL ~~~~");
                Console.WriteLine($"{invocation.ToFullString()}");
                Console.WriteLine($"~~~ END FULL CALL ~~~~");
            }

            if (registeredInterface.TypeKind == TypeKind.Class)
            {
                Console.WriteLine("GetRegistrationForImplementationAndInterface passed a class as interface");
                Console.WriteLine($"~~~ START FULL CALL ~~~~");
                Console.WriteLine($"{invocation.ToFullString()}");
                Console.WriteLine($"~~~ END FULL CALL ~~~~");
            }


            return new RegistrationInfo
            {
                Interface = registeredInterface,
                Implementation = registeredImplementation,
                RegistrationType = regType,
                ProjectName = projectName
            };
        }
        private LifetimeTypes ExtractLifestyle(InvocationExpressionSyntax invocation)
        {
            var fullCall = invocation.ToFullString();

            if (fullCall.Contains("LifestyleTransient"))
                return LifetimeTypes.Transient;
            if (fullCall.Contains("LifestylePerWebRequest") || fullCall.Contains("LifestyleScoped"))
                return LifetimeTypes.PerWebRequest;
            if (fullCall.Contains("LifestyleSingleton"))
                return LifetimeTypes.Singleton;

            return LifetimeTypes.Singleton; // Default for Castle Windsor
        }
    }
}
