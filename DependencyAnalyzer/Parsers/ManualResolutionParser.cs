using DependencyAnalyzer.Comparers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Reflection;

namespace DependencyAnalyzer.Parsers
{
    public class ManualResolutionParser(Solution solution, SolutionAnalyzer solutionAnalyzer) : BaseParser
    {
        public List<ManualResolveInfo> ManuallyResolvedSymbols;
        public List<ManualDisposeInfo> ManuallyDisposedSymbols;

        public async Task Build()
        {
            ManuallyResolvedSymbols = await FindAllManuallyResolvedSymbols();
            ManuallyDisposedSymbols = await FindAllManuallyDisposedSymbols();
        }

        private async Task<List<IMethodSymbol>> GetMethodsFromString(string methodName, string fullyQualifiedTypeName) {
            var ret = new List<IMethodSymbol>();

            // Search all projects for the declaring type
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                var typeSymbol = compilation.GetTypeByMetadataName(fullyQualifiedTypeName);
                if (typeSymbol == null) continue;

                ret.AddRange(typeSymbol
                    .GetMembers(methodName)
                    .OfType<IMethodSymbol>());
            }

            if (ret.Count == 0)
            {
                Console.WriteLine($"[WARN] Could not resolve method {fullyQualifiedTypeName}.{methodName}. No references in solution");
            }

            //dedupe
            ret = ret.GroupBy(m => $"{m.ContainingAssembly.Identity}_{m.ToDisplayString()}")
                    .Select(g => g.First())
                    .ToList();

            return ret;
        }

        private async Task<IEnumerable<ReferencedSymbol>> FindAllReferences(IEnumerable<IMethodSymbol> methods)
        {
            var ret = new List<ReferencedSymbol>();
            foreach (var method in methods)
            {
                ret.AddRange(await SymbolFinder.FindReferencesAsync(method, solution));
            }
            return ret;
        }

        private async Task<List<ManualDisposeInfo>> FindAllManuallyDisposedSymbols()
        {
            var ret = new List<ManualDisposeInfo>();

            var resolveTargets = new[]
            {
                ("Dispose", "System.IDisposable"),
                ("DisposeAsync", "System.IAsyncDisposable"),
                ("Release", "Castle.Windsor.IWindsorContainer"),
            };
            var comparer = new FullyQualifiedNameComparer();

            List<IMethodSymbol> methods = new List<IMethodSymbol>();
            foreach (var (methodName, fullyQualifiedTypeName) in resolveTargets)
            {
                methods.AddRange(await GetMethodsFromString(methodName, fullyQualifiedTypeName));
            }
            var disposeReferences = await FindAllReferences(methods);

            var disposeReferenceLocations = disposeReferences
                .SelectMany(r => r.Locations)
                .Select(l => l.Location)
                .Where(l => l.IsInSource)
                .DistinctBy(l => (l.SourceTree?.FilePath, l.SourceSpan.Start));

            foreach (var location in disposeReferenceLocations)
            {
                var sourceTree = location.SourceTree;
                var span = location.SourceSpan;
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
                    var invocationRoot = new InvocationChainFromRoot
                    {
                        RootClass = disposeCallContainingClass,
                        RootMethod = disposeCallContainingMethod,
                        RootInvocation = disposeInvocation,
                        Project = document.Project.Name,
                        InvocationPath = callPath
                    };

                    ret.AddRange(await GenerateDisposalInfos(disposedClass, disposeInvocation, [invocationRoot], model, solution));
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

                    ret.AddRange(await GenerateDisposalInfos(disposedClass, disposeInvocation, rootInvocations, model, solution));
                }
            }

            return ret;
        }

        private async Task<List<ManualDisposeInfo>> GenerateDisposalInfos(INamedTypeSymbol disposedClass, InvocationExpressionSyntax disposeInvocation, List<InvocationChainFromRoot> rootInvocations, SemanticModel model, Solution solution)
        {
            var ret = new List<ManualDisposeInfo>();

            var typeDecl = disposeInvocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            var containingType = typeDecl != null
                ? model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol
                : null;
            if (containingType == null)
            {
                Console.WriteLine($"Could not find containing class for {disposeInvocation.ToFullString()}");
            }

            foreach (var rootInvocation in rootInvocations)
            {
                //if root is windosr installer expand results for each place it is installed
                if (ImplementsInterface(rootInvocation.RootClass, "Castle.MicroKernel.Registration.IWindsorInstaller"))
                {
                    var projects = await FindInstallerReferencesAsync(solution, rootInvocation.RootClass);
                    foreach (var project in projects)
                    {
                        ret.Add(new ManualDisposeInfo
                        {
                            DisposedType = disposedClass,
                            ContainingType = containingType,
                            CodeSnippet = disposeInvocation.ToFullString(),
                            Project = project,
                            InvocationPath = rootInvocation.InvocationPath
                        });
                    }
                }
                ret.Add(new ManualDisposeInfo
                {
                    DisposedType = disposedClass,
                    ContainingType = containingType,
                    CodeSnippet = disposeInvocation.ToFullString(),
                    Project = rootInvocation.Project,
                    InvocationPath = rootInvocation.InvocationPath
                });
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
                        ret.Add(new InvocationChainFromRoot
                        {
                            RootClass = classSymbol,
                            RootMethod = referencedFromMethod,
                            RootInvocation = invocation,
                            Project = document.Project.Name,
                            InvocationPath = callPath
                        });
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

        public async Task<List<ManualResolveInfo>> FindAllManuallyResolvedSymbols()
        {
            var ret = new List<ManualResolveInfo> { };

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

            List<IMethodSymbol> methods = new List<IMethodSymbol>();
            foreach (var (methodName, fullyQualifiedTypeName) in resolveTargets)
            {
                methods.AddRange(await GetMethodsFromString(methodName, fullyQualifiedTypeName));
            }
            var resolveReferences = await FindAllReferences(methods);

            foreach (var reference in resolveReferences)
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

                    ret.AddRange(GetResolutionInfoFromInvocations(invocation, model, document.Project.Name, document.Name));
                }
            }
            return ret;
        }

        private static bool IsIgnoredClassType(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            var classDeclaration = invocation.Ancestors()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();

            if (classDeclaration != null)
            {
                var classSymbol = model.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                var ignoredNameFragments = new []
                {
                    "Test",
                };

                return ignoredNameFragments.Any(frag => classSymbol.Name.Contains(frag, StringComparison.OrdinalIgnoreCase));
            }

            return false;
        }

        private static List<ManualResolveInfo> GetResolutionInfoFromInvocations(InvocationExpressionSyntax invocation, SemanticModel model, string project, string file)
        {
            var ret = new List<ManualResolveInfo>();
            var typeDecl = invocation.FirstAncestorOrSelf<TypeDeclarationSyntax>();
            var containingType = typeDecl != null
                ? model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol
                : null;
            if (containingType == null)
            {
                Console.WriteLine($"Could not find containing class for {invocation.ToFullString()}");
            }

            foreach (var resolvedType in GetTypeArgumentsFromInvocation(invocation, model))
            {
                ret.Add(new ManualResolveInfo
                {
                    ResolvedType = resolvedType,
                    ContainingType = containingType,
                    Project = project,
                    InvocationPath = file,
                    CodeSnippet = invocation.ToFullString().Trim(),
                    Usage = GetUsageFromResolveInvocation(invocation, model)
                });
            }
            foreach (var resolvedType in GetTypeArgumentsFromInvocationArguments(invocation, model))
            {
                ret.Add(new ManualResolveInfo
                {
                    ResolvedType = resolvedType,
                    ContainingType = containingType,
                    Project = project,
                    InvocationPath = file,
                    CodeSnippet = invocation.ToFullString().Trim(),
                    Usage = GetUsageFromResolveInvocation(invocation, model)
                });
            }

            return ret;
        }

        private static ManualResolveUsage GetUsageFromResolveInvocation(InvocationExpressionSyntax resolveCall, SemanticModel model)
        {
            ManualResolveUsage type;
            var targetSymbol = GetAssignmentTargetSymbol(resolveCall, model);
            if (targetSymbol != null && 
                (targetSymbol is IFieldSymbol || 
                targetSymbol is IPropertySymbol))
            {
                return ManualResolveUsage.Stored;
            }
            else if (resolveCall.Parent is ArgumentSyntax)
            {
                return ManualResolveUsage.Ambiguous;
            }
            
            return ManualResolveUsage.Local;
        }

        private static ISymbol? GetAssignmentTargetSymbol(InvocationExpressionSyntax resolveCall, SemanticModel semanticModel)
        {
            var parent = resolveCall.Parent;

            // Case: var x = container.Resolve<T>();
            if (parent is VariableDeclaratorSyntax variableDeclarator)
            {
                return semanticModel.GetDeclaredSymbol(variableDeclarator);
            }

            // Case: _field = container.Resolve<T>();
            if (parent is AssignmentExpressionSyntax assignment)
            {
                return semanticModel.GetSymbolInfo(assignment.Left).Symbol;
            }

            // Inline use or passed as argument
            return null;
        }
    }
}
