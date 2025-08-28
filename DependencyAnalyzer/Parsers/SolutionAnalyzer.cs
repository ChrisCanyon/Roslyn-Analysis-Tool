using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DependencyAnalyzer.Parsers.MicrosoftDI;
using DependencyAnalyzer.Parsers.Windsor;
using System.Collections.Immutable;
using DependencyAnalyzer.Models;
using DependencyAnalyzer.Comparers;
using System.Diagnostics;

namespace DependencyAnalyzer.Parsers
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

            Console.WriteLine($"Info: Checking projects for dependency frameworks");
            var stopwatch = Stopwatch.StartNew();

            foreach (var p in solution.Projects)
            {
                foreach (var r in p.MetadataReferences)
                {
                    var display = r.Display;
                    if (string.IsNullOrEmpty(display)) continue;

                    var file = Path.GetFileNameWithoutExtension(display);

                    // Castle Windsor
                    if (!usesWindsor && (file.Equals("Castle.Windsor", StringComparison.OrdinalIgnoreCase) ||
                                     display.Contains("Castle.Windsor", StringComparison.OrdinalIgnoreCase)))
                        usesWindsor = true;

                    // Microsoft DI (either assembly triggers true)
                    if (!usesMicrosoftDi && (
                        file.Equals("Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase) ||
                        file.Equals("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.OrdinalIgnoreCase) ||
                        display.Contains("Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase)))
                        usesMicrosoftDi = true;

                    if (usesWindsor && usesMicrosoftDi) break;
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"~~~ Dep Framework Check ~~~");
            Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");

            var allTypes = new List<INamedTypeSymbol>();
            var registrationInfos = new List<RegistrationInfo>();
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

            allTypes.AddRange(allTypesTask.Result);

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
            if (IsSameOrSubclassOf(symbol, "Microsoft.AspNetCore.Mvc.Controller") ||
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
                    ServiceType = null,
                    ImplementationType = symbol,
                    Lifetime = LifetimeTypes.Controller,
                    ProjectName = projectName
                });
                return ret;
            }

            var relatedRegistrations = new List<RegistrationInfo>();

            if (symbol.TypeKind == TypeKind.Interface)
            {
                relatedRegistrations = RegistrationInfos.Where(registration =>
                    comparer.Equals(registration.ServiceType, symbol)
                ).ToList();
            }
            else if (symbol.TypeKind == TypeKind.Class)
            {
                var exactInterfaces = symbol.AllInterfaces.ToImmutableHashSet(comparer);
                var openInterfaces = symbol.AllInterfaces.Select(i => i.OriginalDefinition)
                                    .ToImmutableHashSet(comparer);

                static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol t)
                {
                    for (var b = t.BaseType; b != null; b = b.BaseType)
                        yield return b;
                }
                var exactBases = BaseTypes(symbol).ToImmutableHashSet(comparer);
                var openBases = exactBases
                    .Select(b => b.OriginalDefinition)
                    .ToImmutableHashSet(comparer);

                relatedRegistrations = RegistrationInfos.Where(registration =>
                {
                    //If this is an implementation
                    if (comparer.Equals(registration.ImplementationType, symbol))
                        return true;

                    //Or a type that implements an interface with Unresolvable Implementation (probably weird factory method or registration from assembly)
                    if (registration.UnresolvableImplementation &&
                        registration.ServiceType != null)
                    {
                        // Interfaces
                        if (exactInterfaces.Contains(registration.ServiceType)) return true;                       // closed iface
                        if (openInterfaces.Contains(registration.ServiceType.OriginalDefinition)) return true;     // open generic iface

                        // Base classes
                        if (exactBases.Contains(registration.ServiceType)) return true;                            // closed base
                        if (openBases.Contains(registration.ServiceType.OriginalDefinition)) return true;
                    }
                    return false;
                }
                ).ToList();
            }

            foreach (var registration in relatedRegistrations)
            {
                //I think i need to add the implementation to the factory method here
                //if is factory method and has interface already
                if (symbol.TypeKind == TypeKind.Class && registration.ServiceType != null && registration.ImplementationType == null)
                {
                    var completeRegistration = new RegistrationInfo
                    {
                        ServiceType = registration.ServiceType,
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
            Console.WriteLine($"Info: Loading all types from solution");
            var stopwatch = Stopwatch.StartNew();

            var allSymbols = new List<INamedTypeSymbol>();

            var tasks = new List<Task<List<INamedTypeSymbol>>>();
            foreach (var project in solution.Projects)
            {
                tasks.Add(GetAllTypesInProjectAsync(project));
            }

            await Task.WhenAll(tasks);
            foreach (var task in tasks)
            {
                allSymbols.AddRange(task.Result);
            }

            stopwatch.Stop();
            Console.WriteLine($"~~~ GetAllTypesInSolutionAsync ~~~");
            Console.WriteLine($"\tElapsed time: {stopwatch.ElapsedMilliseconds} ms");
            return allSymbols;
        }

        private static async Task<List<INamedTypeSymbol>> GetAllTypesInProjectAsync(Project project)
        {
            var ret = new List<INamedTypeSymbol>();
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
                return ret;

            var tasks = new List<Task<List<INamedTypeSymbol>>>();
            foreach (var document in project.Documents)
            {
                tasks.Add(GetAllTypesInDocumentAsync(document, compilation));
            }

            await Task.WhenAll(tasks);
            foreach (var task in tasks)
            {
                ret.AddRange(task.Result);
            }
            return ret;
        }

        private static async Task<List<INamedTypeSymbol>> GetAllTypesInDocumentAsync(Document document, Compilation compilation)
        {
            var ret = new List<INamedTypeSymbol>();

            var syntaxTree = await document.GetSyntaxTreeAsync();
            if (syntaxTree == null)
                return ret;

            var root = await syntaxTree.GetRootAsync();
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            if (semanticModel == null)
                return ret;

            var typeDeclarations = root.DescendantNodes()
                .Where(n => n is ClassDeclarationSyntax || n is InterfaceDeclarationSyntax);

            foreach (var decl in typeDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(decl) as INamedTypeSymbol;
                if (symbol != null)
                {
                    ret.Add(symbol);
                }
            }

            return ret;
        }
    }
}
