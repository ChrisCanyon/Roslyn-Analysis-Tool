using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer
{
    public class SolutionAnalyzer
    {
        public Solution Solution { get; private set; }
        public List<INamedTypeSymbol> AllTypes { get; private set; }
        public List<RegistrationInfo> RegistrationInfos { get; private set; }

        public static async Task<SolutionAnalyzer> BuildSolutionAnalyzer(string solutionPath)
        {
            MSBuildLocator.RegisterDefaults();
            using var workspace = MSBuildWorkspace.Create();
            var s = await workspace.OpenSolutionAsync(solutionPath);

            var taskList = new List<Task>();
            taskList.Add(GetAllTypesInSolutionAsync(s));
            taskList.Add(GetSolutionRegistrations(s));

            Task.WaitAll(taskList.ToArray());

            var allTypes = (taskList[0] as Task<List<INamedTypeSymbol>>).Result;
            var registrationInfos = (taskList[1] as Task<List<RegistrationInfo>>).Result; 
            return new SolutionAnalyzer(s, allTypes, registrationInfos);
        }

        public Dictionary<string, RegistrationInfo> GetRegistrationsForSymbol(INamedTypeSymbol symbol)
        {
            var ret = new Dictionary<string, RegistrationInfo>();
            var comparer = new FullyQualifiedNameComparer();

            var relatedRegistrations = RegistrationInfos.Where(x =>
                                            comparer.Equals(x.Implementation, symbol) ||
                                            symbol.Interfaces.Any(y => comparer.Equals(x.Interface, y)));

            foreach (var registration in relatedRegistrations)
            {
                ret.TryAdd(registration.ProjectName, registration);
            }

            return ret;
        }

        private SolutionAnalyzer(Solution solution, List<INamedTypeSymbol> allTypes, List<RegistrationInfo> registrationInfos)
        {
            Solution = solution;
            AllTypes = allTypes;
            RegistrationInfos = registrationInfos;
        }

        private static async Task<List<INamedTypeSymbol>> GetAllTypesInSolutionAsync(Solution solution)
        {
            var allSymbols = new List<INamedTypeSymbol>();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null)
                    continue;

                foreach (var document in project.Documents)
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    if (syntaxTree == null)
                        continue;

                    var root = await syntaxTree.GetRootAsync();
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    if (semanticModel == null)
                        continue;

                    var typeDeclarations = root.DescendantNodes()
                        .Where(n => n is ClassDeclarationSyntax || n is InterfaceDeclarationSyntax);

                    foreach (var decl in typeDeclarations)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(decl) as INamedTypeSymbol;
                        if (symbol != null)
                        {
                            allSymbols.Add(symbol);
                        }
                    }
                }
            }

            return allSymbols;
        }

        private static async Task<List<RegistrationInfo>> GetSolutionRegistrations(Solution solution)
        {
            var ret = new List<RegistrationInfo>();

            var registrationTasks = new List<Task<List<RegistrationInfo>>>();

            foreach (var project in solution.Projects)
            {
                if (project.Name.ToLower().Contains("test")) continue;

                registrationTasks.Add(GetRegistrationsFromProjectAsync(project, solution));
            }

            Task.WaitAll(registrationTasks.ToArray());

            foreach(var task in registrationTasks)
            {
                var projectRegistrations = task.Result;
                ret.AddRange(projectRegistrations);
            }

            return ret;
        }

        private static async Task<List<RegistrationInfo>> GetRegistrationsFromProjectAsync(Project project, Solution solution)
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
                    if(currentInstallerSymbol != null)
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

        private static async Task<List<string>> GetInstallerReferencesAsync(Solution solution, INamedTypeSymbol installerSymbol)
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

        private static INamedTypeSymbol? FindInstallerInDocument(SemanticModel model, SyntaxNode root)
        {
            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl == null) return null;

            var symbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (symbol == null) return null;

            return symbol.AllInterfaces.Any(i => i.Name == "IWindsorInstaller") ? symbol : null;
        }

        private static RegistrationInfo GetRegistrationForFactoryMethod(INamedTypeSymbol registeredSymbol, InvocationExpressionSyntax invocation, string projectName)
        {
            var regType = ExtractLifestyle(invocation);

            if(registeredSymbol.TypeKind == TypeKind.Interface)
            {
                return new RegistrationInfo
                {
                    Interface = registeredSymbol,
                    Implementation = null,
                    RegistrationType = regType,
                    ProjectName = projectName
                };
            }
            else
            {
                return new RegistrationInfo
                {
                    Interface = null,
                    Implementation = registeredSymbol,
                    RegistrationType = regType,
                    ProjectName = projectName
                };
            }
        }

        private static RegistrationInfo GetRegistrationForImplementationOnly(INamedTypeSymbol registeredClass, InvocationExpressionSyntax invocation, string projectName)
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

        private static RegistrationInfo GetRegistrationForImplementationAndInterface(INamedTypeSymbol registeredInterface, INamedTypeSymbol registeredImplementation, InvocationExpressionSyntax invocation, string projectName)
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

        private static List<RegistrationInfo> ParseRegistration(InvocationExpressionSyntax invocation, SemanticModel model, string projectName)
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

        private static LifetimeTypes ExtractLifestyle(InvocationExpressionSyntax invocation)
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
