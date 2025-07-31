using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DependencyAnalyzer.Parsers.MicrosoftDI;
using DependencyAnalyzer.Parsers.Windsor;

namespace DependencyAnalyzer
{
    public class SolutionAnalyzer
    {
        public List<INamedTypeSymbol> AllTypes { get; private set; }
        public List<RegistrationInfo> RegistrationInfos { get; private set; }

        public static async Task<SolutionAnalyzer> Build(Solution solution)
        {
            var allTypesTask = GetAllTypesInSolutionAsync(solution);
 
            var usesWindsor = false;
            var usesMicrosoftDi = false;
 
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var text = document.GetTextAsync().Result.ToString();
 
                    if (text.Contains("Castle.Windsor") || text.Contains("IWindsorContainer"))
                    {
                        usesWindsor = true;
                    }
 
                    if (text.Contains("Microsoft.Extensions.DependencyInjection") || text.Contains("IServiceCollection"))
                    {
                        usesMicrosoftDi = true;
                    }
                }
            }
            var allTypes = new List<INamedTypeSymbol>();
            var registrationInfos =  new List<RegistrationInfo>();
            if (usesMicrosoftDi)
            {
                var registrationHelper = new MicrosoftDIRegistrationParser();
                var registrationInfosTask = registrationHelper.GetSolutionRegistrations(solution);
                await Task.WhenAll(allTypesTask, registrationInfosTask);
                registrationInfos.AddRange(await registrationInfosTask);
            }
            if (usesWindsor)
            {
                var registrationHelper = new WindsorRegistrationParser();
                var registrationInfosTask = registrationHelper.GetSolutionRegistrations(solution);
                await Task.WhenAll(allTypesTask, registrationInfosTask);
                registrationInfos.AddRange(await registrationInfosTask);
            }

            allTypes.AddRange(await allTypesTask);

            return new SolutionAnalyzer(allTypes, registrationInfos);
        }

        private static bool IsController(INamedTypeSymbol symbol)
        {
            if (symbol == null) return false;

            // Check interface implementation (Classic ASP.NET MVC or Web API)
            if (symbol.AllInterfaces.Any(i =>
                i.ToDisplayString() == "System.Web.Mvc.IController" ||
                i.ToDisplayString() == "System.Web.Http.Controllers.IHttpController"))
                return true;

            // Check class name
            if (symbol.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
                return true;

            // Check base types for Controller or ControllerBase
            for (var baseType = symbol.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                var baseName = baseType.ToDisplayString();
                if (baseName == "Microsoft.AspNetCore.Mvc.Controller" ||
                    baseName == "Microsoft.AspNetCore.Mvc.ControllerBase" ||
                    baseName == "System.Web.Mvc.Controller" ||
                    baseName == "System.Web.Http.ApiController")
                    return true;
            }

            // Check for [ApiController] or [Controller] attribute
            if (symbol.GetAttributes().Any(attr =>
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                return attrName == "Microsoft.AspNetCore.Mvc.ApiControllerAttribute" ||
                       attrName == "Microsoft.AspNetCore.Mvc.ControllerAttribute";
            }))
                return true;

            return false;
        }

        public Dictionary<string, RegistrationInfo> GetRegistrationsForSymbol(INamedTypeSymbol symbol)
        {
            var ret = new Dictionary<string, RegistrationInfo>();
            var comparer = new FullyQualifiedNameComparer();

            //if controller pretend its transient
            if (IsController(symbol))
            {
                var projectName = symbol.ContainingAssembly?.Name ?? string.Empty;
                if (projectName == string.Empty) return ret;

                ret.TryAdd(projectName, new RegistrationInfo
                {
                    Interface = null,
                    Implementation = symbol,
                    Lifetime = LifetimeTypes.Controller,
                    ProjectName = projectName
                });
                return ret;
            }


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
                        //Or a type that implements an interface with Unresolvable Implementation (probably weird factory method)
                        (registration.UnresolvableImplementation && 
                            registration.Interface != null && 
                            symbol.Interfaces.Any(currentSymbolInterface => comparer.Equals(registration.Interface, currentSymbolInterface))
                        )).ToList();
            }

            foreach (var registration in relatedRegistrations)
            {
                //I think i need to add the implementation to the factory method here
                //if is factory method and has interface already
                if(symbol.TypeKind == TypeKind.Class && registration.Interface != null && registration.Implementation == null)
                {
                    var completeRegistration = new RegistrationInfo
                    {
                        Interface = registration.Interface,
                        Implementation = symbol,
                        Lifetime = registration.Lifetime,
                        ProjectName = registration.ProjectName,
                    };
                    ret.TryAdd(registration.ProjectName, completeRegistration);
                }
                else
                {
                    ret.TryAdd(registration.ProjectName, registration);
                }
            }

            return ret;
        }

        private SolutionAnalyzer(List<INamedTypeSymbol> allTypes, List<RegistrationInfo> registrationInfos)
        {
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
