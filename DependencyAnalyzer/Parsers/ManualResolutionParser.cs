using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.IO;
using System.Reflection;

namespace DependencyAnalyzer.Parsers
{
    public class ManualResolutionParser(Solution solution, SolutionAnalyzer solutionAnalyzer) : BaseParser
    {
        public List<ManualLifetimeInteractionInfo> ManuallyResolvedSymbols;
        public List<ManualLifetimeInteractionInfo> ManuallyDisposedSymbols;

        public async Task Build()
        {
            ManuallyResolvedSymbols = await FindAllManuallyResolvedSymbols();
            ManuallyDisposedSymbols = await FindAllManuallyDisposedSymbols();
        }

        private async Task<IMethodSymbol?> GetMethodFromString(string methodName, string fullyQualifiedTypeName) {
            IMethodSymbol? methodSymbol = null;

            // Search all projects for the declaring type
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var typeSymbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
                if (typeSymbol == null) continue;

                methodSymbol = typeSymbol
                    .GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault();

                if (methodSymbol != null)
                    break;
            }

            if (methodSymbol == null)
            {
                Console.WriteLine($"[WARN] Could not resolve method {fullyQualifiedTypeName}.{methodName}. No references in solution");
            }
            return methodSymbol;
        }

        private async Task<List<ManualLifetimeInteractionInfo>> FindAllManuallyDisposedSymbols()
        {
            var ret = new List<ManualLifetimeInteractionInfo>();

            var resolveTargets = new[]
            {
      //          ("Dispose", "System.IDisposable"),
      //          ("DisposeAsync", "System.IAsyncDisposable"),
                ("Release", "Castle.Windsor.IWindsorContainer"),
            };
            var comparer = new FullyQualifiedNameComparer();

            foreach (var (methodName, fullyQualifiedTypeName) in resolveTargets)
            {
                var targetMethod = await GetMethodFromString(methodName, fullyQualifiedTypeName);
                if (targetMethod == null) continue;

                var disposeReferences = await SymbolFinder.FindReferencesAsync(targetMethod, solution);
               
                foreach (var reference in disposeReferences)
                {
                    foreach (var location in reference.Locations)
                    {
                        var sourceTree = location.Location.SourceTree;
                        var span = location.Location.SourceSpan;
                        var root = await sourceTree.GetRootAsync();

                        var node = root.FindNode(span);

                        var disposeInvocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();

                        if (disposeInvocation == null) continue;

                        var document = solution.GetDocument(sourceTree);
                        if (document == null) continue;

                        var model = await document.GetSemanticModelAsync();
                        if (model == null) continue;

                        var disposedClass = GetDisposedClass(disposeInvocation, model);
                        if (disposedClass == null || !solutionAnalyzer.AllTypes.Any(x => comparer.Equals(x, disposedClass))) continue;

                        if (IsIgnoredClassType(disposeInvocation, model)) continue;

                        var disposeCallContainingMethodDecl = disposeInvocation.Ancestors()
                            .OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault();
                        if (disposeCallContainingMethodDecl == null) continue;

                        var disposeCallContainingMethod = model.GetDeclaredSymbol(disposeCallContainingMethodDecl) as IMethodSymbol;
                        if (disposeCallContainingMethod == null) continue;

                        var disposeCallContainingClassDecl = disposeInvocation.Ancestors()
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault();
                        if (disposeCallContainingClassDecl == null) continue;

                        var disposeCallContainingClass = model.GetDeclaredSymbol(disposeCallContainingClassDecl) as INamedTypeSymbol;
                        if (disposeCallContainingClass == null) continue;

                        //Sometimes top level is root
                        //ie factory method lambda
                        if (IsRootMethodInovcation(disposeCallContainingClass))
                        {
                            string callPath = $"{disposeCallContainingMethod.ToDisplayString()} -> " + disposeInvocation.ToFullString();
                            var invocationRoot = new InvocationChainFromRoot(
                                disposeCallContainingClass,
                                disposeCallContainingMethod,
                                disposeInvocation,
                                document.Project.Name,
                                callPath
                                );

                            ret.AddRange(await GenerateDisposalInfos(disposedClass, disposeInvocation, [invocationRoot], solution));
                        }
                        else
                        {
                            var visited = new List<IMethodSymbol>();
                            var currentPath = new Stack<string>();
                            var rootInvocations = await WalkInvocationChain(disposeCallContainingMethod, solution, currentPath, visited);

                            if (rootInvocations.Count() == 0)
                            {
                                Console.WriteLine($"Unable to determine root invocation of {disposeCallContainingMethod.ToDisplayString()}:{disposeInvocation.ToFullString()}", ConsoleColor.Cyan);
                            }

                            ret.AddRange(await GenerateDisposalInfos(disposedClass, disposeInvocation, rootInvocations, solution));
                        }
                    }
                }
            }

            return ret;
        }

        private async Task<List<ManualLifetimeInteractionInfo>> GenerateDisposalInfos(INamedTypeSymbol disposedClass, InvocationExpressionSyntax disposeInvocation, List<InvocationChainFromRoot> rootInvocations, Solution solution)
        {
            var ret = new List<ManualLifetimeInteractionInfo>();

            foreach(var rootInvocation in rootInvocations)
            {
                //if root is windosr installer expand results for each place it is installed
                if (ImplementsInterface(rootInvocation.RootClass, "Castle.MicroKernel.Registration.IWindsorInstaller"))
                {
                    var projects = await FindInstallerReferencesAsync(solution, rootInvocation.RootClass);
                    foreach (var project in projects)
                    {
                        ret.Add(new ManualLifetimeInteractionInfo(disposedClass,
                        disposeInvocation.ToFullString(),
                        project,
                        rootInvocation.InvocationPath,
                        ManualLifetimeInteractionKind.Dispose
                        ));
                    }
                }
                ret.Add(new ManualLifetimeInteractionInfo(disposedClass,
                    disposeInvocation.ToFullString(),
                    rootInvocation.Project,
                    rootInvocation.InvocationPath,
                    ManualLifetimeInteractionKind.Dispose
                    ));
            }

            return ret;
        }

        private async Task<List<InvocationChainFromRoot>> WalkInvocationChain(IMethodSymbol method, Solution solution, Stack<string> path, List<IMethodSymbol> visited)
        {
            var ret = new List<InvocationChainFromRoot>();

            if (path.Contains(method.ToDisplayString())) return ret;
            if (visited.Any(x => x.ToDisplayString() == method.ToDisplayString())) return ret;

            path.Push(method.ToDisplayString());

            var references = await SymbolFinder.FindReferencesAsync(method, solution);
            foreach (var reference in references)
            {
                foreach (var location in reference.Locations)
                {
                    var sourceTree = location.Location.SourceTree;
                    var span = location.Location.SourceSpan;
                    var root = await sourceTree.GetRootAsync();

                    var node = root.FindNode(span);

                    var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    if (invocation == null) continue;

                    var document = solution.GetDocument(sourceTree);
                    if (document == null) continue;

                    var model = await document.GetSemanticModelAsync();
                    if (model == null) continue;

                    if (IsIgnoredClassType(invocation, model)) continue;

                    var methodDecl = invocation.Ancestors()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();
                    if (methodDecl == null) continue;

                    var referencedFromMethod = model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                    if (referencedFromMethod == null) continue;

                    var referencedFromClass = invocation.Ancestors()
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault();
                    if (referencedFromClass == null) continue;

                    var classSymbol = model.GetDeclaredSymbol(referencedFromClass) as INamedTypeSymbol;
                    if (classSymbol == null) continue;

                    if (IsRootMethodInovcation(classSymbol))
                    {
                        string callPath = $"{referencedFromMethod.ToDisplayString()} -> " + string.Join(" -> ", path);
                        ret.Add(new InvocationChainFromRoot(
                            classSymbol,
                            referencedFromMethod,
                            invocation,
                            document.Project.Name,
                            callPath
                            ));
                    }
                    else
                    {
                        ret.AddRange(await WalkInvocationChain(referencedFromMethod, solution, path, visited));
                    }
                }
            }

            path.Pop();

            return ret;
        }

        private bool IsRootMethodInovcation(INamedTypeSymbol classSymbol){
            //check if task
            if (ImplementsInterface(classSymbol, "Quartz.IJob")) 
                return true;
            
            //check if controller
            if (IsSameOrSubclassOf(classSymbol, "Microsoft.AspNetCore.Mvc.ControllerBase") ||
                IsSameOrSubclassOf(classSymbol, "Microsoft.AspNetCore.Mvc.Controller") ||
                IsSameOrSubclassOf(classSymbol, "System.Web.Mvc.Controller") ||
                IsSameOrSubclassOf(classSymbol, "System.Web.Http.ApiController")
                ) return true;

            //check if startup.cs
            if ((classSymbol.Name is "Startup" or "Program") ||
                ImplementsInterface(classSymbol, "Castle.MicroKernel.Registration.IWindsorInstaller") ||
                IsSameOrSubclassOf(classSymbol, "System.Web.HttpApplication"))
                return true;

            return false;
        }

        private static INamedTypeSymbol? GetDisposedClass(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return null;
            //release is passed an object to release
            if (memberAccess.Name.Identifier.Text.StartsWith("Release"))
            {
                var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (symbol != null)
                {
                    var arg = invocation.ArgumentList.Arguments.First();
                    var typeInfo = model.GetTypeInfo(arg.Expression);
                    if(typeInfo.Type is INamedTypeSymbol disposedClass)
                    {
                        return disposedClass;
                    }
                }
            //dispose is called on an object to be disposed
            }else if (memberAccess.Name.Identifier.Text.StartsWith("Dispose"))
            {
                var disposedClass = GetCallingClassFromInvocation(invocation, model);
                if(disposedClass != null)
                {
                    return disposedClass;
                }
            }

            return null;
        }

        public async Task<List<ManualLifetimeInteractionInfo>> FindAllManuallyResolvedSymbols()
        {
            var ret = new List<ManualLifetimeInteractionInfo> { };

            var resolveTargets = new[]
                {
                    // System.IServiceProvider (interface)
                    ("GetRequiredService", "System.IServiceProvider"),
                    ("GetService", "System.IServiceProvider"),
                    ("GetServices", "System.IServiceProvider"),

                    // System.IServiceProvider (extensions)
                    ("GetRequiredService", "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"),
                    ("GetService", "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"),
                    ("GetServices", "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"),

                    // System.Web.Mvc.IDependencyResolver (interface)
                    ("GetService", "System.Web.Mvc.IDependencyResolver"),

                    // System.Web.Mvc.IDependencyResolver (extensions)
                    ("GetService", "System.Web.Mvc.DependencyResolverExtensions"),

                    // Castle.MicroKernel.IKernel
                    ("Resolve", "Castle.MicroKernel.IKernel"),
                    ("ResolveAll", "Castle.MicroKernel.IKernel"),

                    // Castle.Windsor.IWindsorContainer
                    ("Resolve", "Castle.Windsor.IWindsorContainer"),
                    ("ResolveAll", "Castle.Windsor.IWindsorContainer")
                };

            foreach (var (methodName, fullyQualifiedTypeName) in resolveTargets)
            {
                var method = await GetMethodFromString(methodName, fullyQualifiedTypeName);

                if (method == null) continue;

                var references = await SymbolFinder.FindReferencesAsync(method, solution);

                foreach (var reference in references)
                {
                    foreach (var location in reference.Locations)
                    {
                        var sourceTree = location.Location.SourceTree;
                        var span = location.Location.SourceSpan;
                        var root = await sourceTree.GetRootAsync();

                        var node = root.FindNode(span);

                        var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();

                        if (invocation == null) continue;

                        var document = solution.GetDocument(sourceTree);
                        if (document == null) continue;

                        var model = await document.GetSemanticModelAsync();
                        if (model == null) continue;

                        if (IsIgnoredClassType(invocation, model)) continue;

                        ret.AddRange(GetResolutionInfoFromInvocations(new[] { invocation }, model, document.Project.Name, document.Name));
                    }
                }
            }
            return ret;
        }

        private static bool IsIgnoredClassType(InvocationExpressionSyntax invocation, SemanticModel model)
        {
          //  return false;
            var classDeclaration = invocation.Ancestors()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();

            if (classDeclaration != null)
            {
                var classSymbol = model.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                var ignoredNameFragments = new []
                {
    //                "Build",
      //              "Factory",
                    "Resolver",
                    "Test",
                };

                return ignoredNameFragments.Any(frag => classSymbol.Name.Contains(frag, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private static List<ManualLifetimeInteractionInfo> GetResolutionInfoFromInvocations(IEnumerable<InvocationExpressionSyntax> invocations, SemanticModel model, string project, string file)
        {
            var ret = new List<ManualLifetimeInteractionInfo>();
            foreach (var invocation in invocations)
            {
                foreach (var resolvedType in GetTypeArgumentsFromInvocation(invocation, model))
                {
                    ret.Add(new ManualLifetimeInteractionInfo(
                        resolvedType,
                        project,
                        file,
                        invocation.ToFullString().Trim(),
                        ManualLifetimeInteractionKind.Resolve
                    ));
                }
                foreach (var resolvedType in GetTypeArgumentsFromInvocationArguments(invocation, model))
                {
                    ret.Add(new ManualLifetimeInteractionInfo(
                        resolvedType,
                        project,
                        file,
                        invocation.ToFullString().Trim(),
                        ManualLifetimeInteractionKind.Resolve
                    ));
                }
            }

            return ret;
        }

    }
}
