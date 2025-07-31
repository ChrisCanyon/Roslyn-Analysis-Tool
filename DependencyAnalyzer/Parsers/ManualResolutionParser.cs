using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer.Parsers
{
    public class ManualResolutionParser : BaseParser
    {
        public List<ManualResolutionInfo> ManuallyResolvedSymbols;
        public List<ManualResolutionInfo> ManuallyDisposedSymbols;

        public static async Task<ManualResolutionParser> Build(Solution s)
        {
            return new ManualResolutionParser(
                    await FindAllManuallyResolvedSymbols(s),
                    await FindAllManuallyDisposedSymbols(s)
                );
        }

        private ManualResolutionParser(List<ManualResolutionInfo> resolved, List<ManualResolutionInfo> disposed)
        {
            ManuallyResolvedSymbols = resolved;
            ManuallyDisposedSymbols = disposed;
        }

        public static async Task<List<ManualResolutionInfo>> FindAllManuallyDisposedSymbols(Solution solution)
        {
            return new List<ManualResolutionInfo>();
        }

        //TODO ignore factory methods
        public static async Task<List<ManualResolutionInfo>> FindAllManuallyResolvedSymbols(Solution solution)
        {
            var ret = new List<ManualResolutionInfo> { };

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
                        ret.AddRange(GetResolutionInfosFromInvocations(invocations, model, project.Name, doc.Name));
                    }
                }
            }

            return ret;
        }

        private static List<ManualResolutionInfo> GetResolutionInfosFromInvocations(IEnumerable<InvocationExpressionSyntax> invocations, SemanticModel model, string project, string file)
        {
            var ret = new List<ManualResolutionInfo>();
            foreach (var invocation in invocations)
            {
                foreach (var resolvedType in GetTypeArgumentsFromInvocation(invocation, model))
                {
                    ret.Add(new ManualResolutionInfo(
                        resolvedType,
                        project,
                        file,
                        invocation.ToFullString().Trim()
                    ));
                }
                foreach (var resolvedType in GetTypeArgumentsFromInvocationArguments(invocation, model))
                {
                    ret.Add(new ManualResolutionInfo(
                        resolvedType,
                        project,
                        file,
                        invocation.ToFullString().Trim()
                    ));
                }
            }

            return ret;
        }

    }
}
