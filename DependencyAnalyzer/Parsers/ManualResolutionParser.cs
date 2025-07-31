using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;

namespace DependencyAnalyzer.Parsers
{
    public class ManualResolutionParser : BaseParser
    {
        public List<ManualLifetimeInteractionInfo> ManuallyResolvedSymbols;
        public List<ManualLifetimeInteractionInfo> ManuallyDisposedSymbols;

        public static async Task<ManualResolutionParser> Build(Solution s)
        {
            return new ManualResolutionParser(
                    await FindAllManuallyResolvedSymbols(s),
                    await FindAllManuallyDisposedSymbols(s)
                );
        }

        private ManualResolutionParser(List<ManualLifetimeInteractionInfo> resolved, List<ManualLifetimeInteractionInfo> disposed)
        {
            ManuallyResolvedSymbols = resolved;
            ManuallyDisposedSymbols = disposed;
        }

        public static async Task<List<ManualLifetimeInteractionInfo>> FindAllManuallyDisposedSymbols(Solution solution)
        {
            var ret = new List<ManualLifetimeInteractionInfo> { };

            var resolveTargets = new[]
                {
                    ("Dispose", "System.IDisposable"),
                    ("DisposeAsync", "System.IAsyncDisposable"),
                    ("Release", "Castle.Windsor.IWindsorContainer"),
                };

            foreach (var project in solution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    var root = await doc.GetSyntaxRootAsync();

                    var model = await doc.GetSemanticModelAsync();

                    if (root == null || model == null) continue;

                    foreach (var (methodName, containerType) in resolveTargets)
                    {
                        var invocations = FindInvocations(root, model, methodName, containerType);
                        ret.AddRange(GetDisposalInfoFromInovaction(invocations, model, project.Name, doc.Name));
                    }
                }
            }

            return ret;
        }

        private static List<ManualLifetimeInteractionInfo> GetDisposalInfoFromInovaction(IEnumerable<InvocationExpressionSyntax> invocations, SemanticModel model, string project, string file)
        {
            var ret = new List<ManualLifetimeInteractionInfo>();
            foreach (var invocation in invocations)
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;
                if (memberAccess.Name.Identifier.Text.StartsWith("Release"))
                {
                    var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol != null)
                    {
                        var arg = invocation.ArgumentList.Arguments.First();
                        var typeInfo = model.GetTypeInfo(arg.Expression);
                        if(typeInfo.Type is INamedTypeSymbol disposedClass)
                        {
                            ret.Add(new ManualLifetimeInteractionInfo(
                                disposedClass,
                                project,
                                file,
                                invocation.ToFullString().Trim(),
                                ManualLifetimeInteractionKind.Dispose));
                        }
                    }
                }
                    
                //Object.Dispose
                if (memberAccess.Name.Identifier.Text.StartsWith("Dispose"))
                {
                    var disposedClass = GetCallingClassFromInvocation(invocation, model);
                    if(disposedClass != null)
                    {
                        ret.Add(new ManualLifetimeInteractionInfo(
                            disposedClass,
                            project,
                            file,
                            invocation.ToFullString().Trim(),
                            ManualLifetimeInteractionKind.Dispose
                    ));
                    }
                }
            }

            return ret;
        }

        //TODO ignore factory methods
        public static async Task<List<ManualLifetimeInteractionInfo>> FindAllManuallyResolvedSymbols(Solution solution)
        {
            var ret = new List<ManualLifetimeInteractionInfo> { };

            var resolveTargets = new[]
                {
                    //System.IServiceProvider
                    ("GetRequiredService", "System.IServiceProvider"),
                    ("GetService", "System.IServiceProvider"),
                    ("GetServices", "System.IServiceProvider"),

                    //System.Web.Mvc.IDependencyResolver
                    ("GetService", "System.Web.Mvc.IDependencyResolver"),

                    //Castle.MicroKernel.IKernel
                    ("Resolve", "Castle.MicroKernel.IKernel"),
                    ("ResolveAll", "Castle.MicroKernel.IKernel"),

                    //Castle.Windsor.IWindsorContainer
                    ("Resolve", "Castle.Windsor.IWindsorContainer"),
                    ("ResolveAll", "Castle.Windsor.IWindsorContainer")
                };

            foreach (var project in solution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    var root = await doc.GetSyntaxRootAsync();

                    var model = await doc.GetSemanticModelAsync();

                    if (root == null || model == null) continue;

                    foreach (var (methodName, containerType) in resolveTargets)
                    {
                        var invocations = FindInvocations(root, model, methodName, containerType);
                        ret.AddRange(GetResolutionInfoFromInvocations(invocations, model, project.Name, doc.Name));
                    }
                }
            }

            return ret;
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
