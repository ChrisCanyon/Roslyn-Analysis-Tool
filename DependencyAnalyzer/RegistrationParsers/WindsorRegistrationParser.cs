using DependencyAnalyzer.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.RegistrationParsers
{
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
                var invocations = root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv =>
                        inv.Expression is MemberAccessExpressionSyntax memberAccess &&
                        memberAccess.Name.Identifier.Text == "Register" &&
                        inv.ToString().Contains("Component.For"))
                    .ToList();

                INamedTypeSymbol? currentInstallerSymbol = FindInstallerInDocument(model, root);
                List<string> calledProjects = [];
                if (currentInstallerSymbol != null) calledProjects = await GetInstallerReferencesAsync(solution, currentInstallerSymbol);

                foreach (var invocation in invocations)
                {
                    // Try to parse this invocation into a structured RegistrationInfo object
                    var registration = ParseRegistration(invocation, model, project.Name);

                    //Clean up installer junk
                    if (currentInstallerSymbol != null && calledProjects.Count > 0)
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

        private INamedTypeSymbol? FindInstallerInDocument(SemanticModel model, SyntaxNode root)
        {
            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl == null) return null;

            var symbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (symbol == null) return null;

            return symbol.AllInterfaces.Any(i => i.Name == "IWindsorInstaller") ? symbol : null;
        }

        private async Task<List<string>> GetInstallerReferencesAsync(Solution solution, INamedTypeSymbol installerSymbol)
        {
            var result = new List<string>();
            var installerName = installerSymbol.Name;

            var comparer = new FullyQualifiedNameComparer();
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync();
                    var model = await document.GetSemanticModelAsync();
                    if (root == null || model == null) continue;

                    var installCalls = root.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Where(inv =>
                            inv.Expression is MemberAccessExpressionSyntax memberAccess &&
                            memberAccess.Name.Identifier.Text == "Install")
                        .ToList();

                    foreach (var call in installCalls)
                    {
                        var argumentTypes = call.ArgumentList.Arguments
                            .Select(arg => model.GetTypeInfo(arg.Expression).Type)
                            .OfType<INamedTypeSymbol>();

                        foreach (var argSymbol in argumentTypes)
                        {
                            if (comparer.Equals(argSymbol, installerSymbol))
                            {
                                result.Add(project.Name);
                                break; // No need to check other args in this call
                            }
                        }
                    }
                }
            }

            return result.Distinct().ToList();
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
