using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Editing;

namespace DependencyAnalyzer._Random
{
    public class rewriter
    {

        static string GetLifestyleString(InvocationExpressionSyntax invocation)
        {
            var fullText = invocation.ToFullString();

            if (fullText.Contains("Lifestyle.Transient"))
                return ".LifestyleTransient()";
            if (fullText.Contains("Lifestyle.Singleton"))
                return ".LifestyleSingleton()";
            // Default (Castle Windsor default is Singleton)
            return "";
        }

        public static async Task RewriteRegisterForAsync(string solutionPath, string fullyQualifiedClassName)
        {
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            var editorWorkspace = new AdhocWorkspace();

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation == null) continue;

                foreach (var document in project.Documents)
                {
                    var model = await document.GetSemanticModelAsync();
                    var root = await document.GetSyntaxRootAsync();
                    if (root == null || model == null) continue;

                    var classDecls = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
                    foreach (var classDecl in classDecls)
                    {
                        var symbol = model.GetDeclaredSymbol(classDecl);
                        if (symbol?.ToDisplayString() != fullyQualifiedClassName)
                            continue;

                        var editor = await DocumentEditor.CreateAsync(document);
                        var invocations = classDecl.DescendantNodes().OfType<InvocationExpressionSyntax>();

                        foreach (var invocation in invocations)
                        {
                            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                memberAccess.Name is GenericNameSyntax genericName)
                            {
                                var methodName = genericName.Identifier.Text;
                                var typeArgs = genericName.TypeArgumentList.Arguments;

                                if (methodName == "RegisterFor" && typeArgs.Count == 2)
                                {
                                    var interfaceType = genericName.TypeArgumentList.Arguments[0];
                                    var implementationType = genericName.TypeArgumentList.Arguments[1];

                                    var lifestyleCall = GetLifestyleString(invocation);
                                    var newExpr = SyntaxFactory.ParseExpression(
                                        $"container.Register(Component.For<{interfaceType}>().ImplementedBy<{implementationType}>(){lifestyleCall})"
                                    ).WithTriviaFrom(invocation);

                                    editor.ReplaceNode(invocation, newExpr);
                                }
                            }
                        }

                        var newDoc = editor.GetChangedDocument();
                        var newRoot = await newDoc.GetSyntaxRootAsync();
                        if (newRoot != null)
                        {
                            workspace.TryApplyChanges(newDoc.Project.Solution);
                            Console.WriteLine($"Rewrote RegisterFor in: {document.FilePath}");
                        }
                    }
                }
            }
        }
    }
}
