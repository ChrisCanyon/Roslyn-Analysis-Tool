using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DependencyAnalyzer.Parsers.MicrosoftDI;
using DependencyAnalyzer.Parsers.Windsor;
using DependencyAnalyzer.Parsers;
using System.Collections.Immutable;

namespace DependencyAnalyzer
{
    public class SolutionAnalyzer : BaseParser
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
            if (ImplementsInterface(symbol, "System.Web.Mvc.IController") ||
                ImplementsInterface(symbol, "System.Web.Http.Controllers.IHttpController"))
                return true;

            // Check class name
            if (symbol.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
                return true;

            // Check base types for Controller or ControllerBase
            if(IsSameOrSubclassOf(symbol, "Microsoft.AspNetCore.Mvc.Controller") ||
                IsSameOrSubclassOf(symbol, "Microsoft.AspNetCore.Mvc.ControllerBase") ||
                IsSameOrSubclassOf(symbol, "System.Web.Mvc.Controller") ||
                IsSameOrSubclassOf(symbol, "System.Web.Http.ApiController"))
                    return true;

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

        public List<RegistrationInfo> GetRegistrationsForSymbol(INamedTypeSymbol symbol)
        {
            var ret = new List<RegistrationInfo>();
            var comparer = new FullyQualifiedNameComparer();

            //if controller pretend its transient
            if (IsController(symbol))
            {
                var projectName = symbol.ContainingAssembly?.Name ?? string.Empty;
                if (projectName == string.Empty) return ret;

                ret.Add(new RegistrationInfo
                {
                    ServiceInterface = null,
                    ImplementationType = symbol,
                    Lifetime = LifetimeTypes.Controller,
                    ProjectName = projectName
                });
                return ret;
            }

            var relatedRegistrations = new List<RegistrationInfo>();

            if(symbol.TypeKind == TypeKind.Interface)
            {
                relatedRegistrations = RegistrationInfos.Where(registration =>
                    comparer.Equals(registration.ServiceInterface, symbol)
                ).ToList();
            }
            else if(symbol.TypeKind == TypeKind.Class)
            {
                var exactInterface = symbol.AllInterfaces.ToImmutableHashSet(comparer);
                var openInterfaces = symbol.AllInterfaces.Select(i => i.OriginalDefinition)
                                    .ToImmutableHashSet(comparer);

                relatedRegistrations = RegistrationInfos.Where(registration => {
                    //If this is an implementation
                    if (comparer.Equals(registration.ImplementationType, symbol)) 
                        return true;

                    //Or a type that implements an interface with Unresolvable Implementation (probably weird factory method or registration from assembly)
                    if (registration.UnresolvableImplementation &&
                        registration.ServiceInterface != null)
                    {
                        // 1) constructed match: IClass<A>
                        if (exactInterface.Contains(registration.ServiceInterface))
                            return true;

                        // 2) open generic match: IClass<> vs IClass<A>
                        if (openInterfaces.Contains(registration.ServiceInterface.OriginalDefinition))
                            return true;
                    }
                    return false;
                }
                ).ToList();
            }

            foreach (var registration in relatedRegistrations)
            {
                //I think i need to add the implementation to the factory method here
                //if is factory method and has interface already
                if(symbol.TypeKind == TypeKind.Class && registration.ServiceInterface != null && registration.ImplementationType == null)
                {
                    var completeRegistration = new RegistrationInfo
                    {
                        ServiceInterface = registration.ServiceInterface,
                        ImplementationType = symbol,
                        Lifetime = registration.Lifetime,
                        ProjectName = registration.ProjectName,
                    };
                    ret.Add(completeRegistration);
                }
                else
                {
                    ret.Add(registration);
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
