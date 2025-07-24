using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DependencyAnalyzer.Interfaces;

namespace DependencyAnalyzer
{
    public class SolutionAnalyzer
    {
        public Solution Solution { get; private set; }
        public List<INamedTypeSymbol> AllTypes { get; private set; }
        public List<RegistrationInfo> RegistrationInfos { get; private set; }

        public static async Task<SolutionAnalyzer> BuildSolutionAnalyzer(string solutionPath, IRegistrationHelper regirationHelper)
        {
            MSBuildLocator.RegisterDefaults();
            using var workspace = MSBuildWorkspace.Create();
            var s = await workspace.OpenSolutionAsync(solutionPath);

            var allTypesTask = GetAllTypesInSolutionAsync(s);
            var registrationInfosTask = regirationHelper.GetSolutionRegistrations(s);

            await Task.WhenAll(allTypesTask, registrationInfosTask);

            var allTypes = await allTypesTask;
            var registrationInfos = await registrationInfosTask; 
            return new SolutionAnalyzer(s, allTypes, registrationInfos);
        }

        public Dictionary<string, RegistrationInfo> GetRegistrationsForSymbol(INamedTypeSymbol symbol)
        {
            var ret = new Dictionary<string, RegistrationInfo>();
            var comparer = new FullyQualifiedNameComparer();

            var relatedRegistrations = new List<RegistrationInfo>();

            if(symbol.TypeKind == TypeKind.Interface)
            {
                relatedRegistrations = RegistrationInfos.Where(registration =>
                    comparer.Equals(registration.Interface, symbol)
                ).ToList();
            }
            else if(symbol.TypeKind == TypeKind.Class)
            {
                relatedRegistrations = RegistrationInfos.Where(registration =>
                        //If this is an implementation
                        comparer.Equals(registration.Implementation, symbol) ||
                        //Or a type that implements a factory interface
                        (registration.IsFactoryMethod && // Dont have good parsing for factory methods.
                            registration.Interface != null && // Assume any implementation of the interface could be registered
                            symbol.Interfaces.Any(currentSymbolInterface => comparer.Equals(registration.Interface, currentSymbolInterface))
                        )).ToList();
            }


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
    }
}
