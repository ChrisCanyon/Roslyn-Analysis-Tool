using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DependencyAnalyzer
{
    public static class SolutionAnalyzer
    {
        public static async Task<List<INamedTypeSymbol>> GetAllClassesInSolutionAsync(string solutionPath)
        {
            MSBuildLocator.RegisterDefaults();
            var classSymbols = new List<INamedTypeSymbol>();

            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    if (syntaxTree == null) continue;

                    var root = await syntaxTree.GetRootAsync();
                    var semanticModel = await document.GetSemanticModelAsync();
                    if (semanticModel == null) continue;

                    var classDeclarations = root.DescendantNodes()
                        .OfType<ClassDeclarationSyntax>();

                    foreach (var classDecl in classDeclarations)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                        if (symbol != null)
                        {
                            classSymbols.Add(symbol);
                        }
                    }
                }
            }

            return classSymbols;
        }
    }
}
