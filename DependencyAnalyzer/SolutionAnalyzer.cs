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
            var allTypes = await GetAllTypesInSolutionAsync(s);
            var registrationInfos = await GetSolutionRegistrations(s);
            return new SolutionAnalyzer(s, allTypes, registrationInfos);
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

            foreach(var project in solution.Projects)
            {
                var projectRegistrations = await GetRegistrationsFromProjectAsync(project);
                ret.AddRange(projectRegistrations);
            }

            return ret;
        }

        private static async Task<List<RegistrationInfo>> GetRegistrationsFromProjectAsync(Project project)
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
                    // Look for: container.Register(Component.For<...>().ImplementedBy<...>().LifestyleX())
                    if (!invocation.ToString().Contains("Component.For")) continue;

                    var registration = ParseRegistration(invocation, model, project.Name);
                    if (registration != null)
                        registrations.Add(registration);
                }
            }

            return registrations;
        }

        private static RegistrationInfo? ParseRegistration(InvocationExpressionSyntax invocation, SemanticModel model, string projectName)
        {
            // Look for the `Component.For<T>().ImplementedBy<U>().LifestyleX()` chain
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var fullCall = memberAccess.ToString();

                // Try to resolve the type arguments in For<>() and ImplementedBy<>()
                var genericNodes = invocation.DescendantNodes().OfType<TypeOfExpressionSyntax>().ToList();
                var genericTypes = invocation.DescendantNodes().OfType<GenericNameSyntax>();

                var allTypes = genericTypes.SelectMany(g =>
                    g.TypeArgumentList.Arguments.Select(arg => model.GetSymbolInfo(arg).Symbol as INamedTypeSymbol))
                    .Where(s => s != null)
                    .Cast<INamedTypeSymbol>()
                    .ToList();

                if (allTypes.Count >= 2)
                {
                    var interfaceType = allTypes[0];
                    var implementationType = allTypes[1];

                    var regType = LifetimeTypes.Singleton;
                    if (fullCall.Contains("LifestyleTransient")) regType = LifetimeTypes.Transient;
                    else if (fullCall.Contains("LifestylePerWebRequest")) regType = LifetimeTypes.PerWebRequest;

                    return new RegistrationInfo
                    {
                        Interface = interfaceType,
                        Implementation = implementationType,
                        RegistrationType = regType,
                        ProjectName = projectName
                    };
                }
            }

            return null;
        }
    
        public Dictionary<string, RegistrationInfo> GetRegistrationsForSymbol(INamedTypeSymbol symbol)
        {
            var ret = new Dictionary<string, RegistrationInfo>();
            var comparer = new FullyQualifiedNameComparer();

            foreach(var registration in RegistrationInfos.Where(x => comparer.Equals(x.Implementation, symbol)))
            {
                ret.Add(registration.ProjectName, registration);
            }

            return ret;
        }
    }
    public class FullyQualifiedNameComparer : IEqualityComparer<INamedTypeSymbol>
    {
        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
        {
            if (x is null || y is null)
                return false;

            return GetKey(x) == GetKey(y);
        }

        public int GetHashCode(INamedTypeSymbol obj)
        {
            return GetKey(obj).GetHashCode();
        }

        private string GetKey(INamedTypeSymbol symbol)
        {
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}
