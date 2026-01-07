using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using RandomCodeAnalysis.Models.MethodChain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;

namespace RandomCodeAnalysis
{
    public class CallChainAsyncConverter
    {
        public async Task ConvertMethodChainToAsync(MethodReferenceNode topNode, Solution solution)
        {
            var editedSolution = await RecurseCallChain(topNode, solution);

            await RATWriter.SaveSolutionToDiskAsync(editedSolution, solution);
        }

        private async Task<Solution> RecurseCallChain(MethodReferenceNode node, Solution solution)
        {
            //Convert Method Signatures to Async
            if (node.ReferencedMethod.MethodKind == MethodKind.Ordinary ||
                node.ReferencedMethod.MethodKind == MethodKind.LocalFunction)
            {
                if (node.ReferencedMethod.IsAsync)
                {
                    return solution;
                }

                //Convert Signature
                var (success, updatedSolution, reason) = await UpdateMethodSignature(node.ReferencedMethod, solution);
                solution = updatedSolution;
                if (!success)
                {
                    solution = await WriteCommentNearMethodDecls(reason ?? "COULD NOT CONVERT (unknown reason)", node.ReferencedMethod, solution);
                    return solution;
                }

                // rename
                solution = await RenameMethodSymbolToAsync(node.ReferencedMethod, solution);

            }
            else
            {
                //Leave comment
                solution = await WriteCommentNearMethodDecls("Cannot convert: MethodKind is unsupported", node.ReferencedMethod, solution);
                return solution;
            }

            // Convert Call Sites to Await (grouped per doc to avoid span drift)
            foreach (var group in node.CallSites.GroupBy(cs => cs.DocumentId))
            {
                solution = await UpdateMethodReferenceSitesToAsync(group.Key, group, solution)
                    .ConfigureAwait(false);
            }

            //Recurse Graph
            foreach (var referenceNode in node.CallerNodes)
            {
                solution = await RecurseCallChain(referenceNode, solution);
            }

            return solution;
        }

        #region Method Update Logic
        private async Task<Solution> RenameMethodSymbolToAsync(IMethodSymbol method, Solution solution)
        {
            // Don’t double-rename
            if (method.Name.EndsWith("Async", StringComparison.Ordinal))
                return solution;

            var newName = method.Name + "Async";

            var options = new SymbolRenameOptions(
                RenameOverloads: true,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);

            var newSolution = await Renamer.RenameSymbolAsync(
                solution,
                method,
                options,
                newName,
                CancellationToken.None);

            return newSolution;
        }

        private async Task<Solution> WriteCommentNearMethodDecls(string comment, IMethodSymbol method, Solution solution)
        {
            var declarations = await GetMethodDeclarationsAsync(method);
            if (declarations.Count == 0)
                return solution;

            var commentTrivia = CreateLeadingCommentTrivia(comment);

            // Edit per document (SyntaxTree -> Document)
            foreach (var group in declarations.GroupBy(n => n.SyntaxTree))
            {
                var document = solution.GetDocument(group.Key);
                if (document == null)
                    continue;

                var editor = await DocumentEditor.CreateAsync(document).ConfigureAwait(false);

                foreach (var node in group)
                {
                    AddCannotConvertComment(editor, node, commentTrivia);
                }

                solution = editor.GetChangedDocument().Project.Solution;
            }

            return solution;
        }

        private async Task<(bool Success, Solution Solution, string? FailureReason)> UpdateMethodSignature(
            IMethodSymbol method,
            Solution solution)
        {
            try
            {
                if (!CanConvertMethod(method))
                    return (false, solution, "Cannot convert: method is null/extern/unsupported kind/no declarations.");

                var declarations = await GetMethodDeclarationsAsync(method);
                if (declarations.Count == 0)
                    return (false, solution, "No declaring syntax references found.");

                // Apply edits per document
                foreach (var group in declarations.GroupBy(d => d.SyntaxTree))
                {
                    var doc = solution.GetDocument(group.Key);
                    if (doc == null)
                        return (false, solution, "Could not resolve Document for declaration SyntaxTree.");

                    var editor = await DocumentEditor.CreateAsync(doc).ConfigureAwait(false);

                    foreach (var oldDecl in group)
                    {
                        ApplySignatureUpdateOrComment(editor, method, oldDecl);
                    }

                    solution = editor.GetChangedDocument().Project.Solution;
                }


                return (true, solution, null);
            }
            catch (Exception ex)
            {
                return (false, solution, ex.GetType().Name);
            }
        }

        private void ApplySignatureUpdateOrComment(DocumentEditor editor, IMethodSymbol method, SyntaxNode oldDecl)
        {
            switch (method.MethodKind)
            {
                case MethodKind.Ordinary:
                    {
                        if (oldDecl is MethodDeclarationSyntax m)
                        {
                            var newDecl = ConvertMethodDeclaration(method, m)
                                .WithAdditionalAnnotations(Formatter.Annotation);
                            editor.ReplaceNode(m, newDecl);
                        }
                        else
                        {
                            AddCannotConvertComment(editor, oldDecl,
                                CreateLeadingCommentTrivia($"Expected MethodDeclarationSyntax for Ordinary method, got {oldDecl.GetType().Name}"));
                        }
                        break;
                    }

                case MethodKind.LocalFunction:
                    {
                        if (oldDecl is LocalFunctionStatementSyntax lf)
                        {
                            var newDecl = ConvertLocalFunctionDeclaration(method, lf)
                                .WithAdditionalAnnotations(Formatter.Annotation);
                            editor.ReplaceNode(lf, newDecl);
                        }
                        else
                        {
                            AddCannotConvertComment(editor, oldDecl,
                                CreateLeadingCommentTrivia($"Expected LocalFunctionStatementSyntax for LocalFunction, got {oldDecl.GetType().Name}"));
                        }
                        break;
                    }

                default:
                    {
                        AddCannotConvertComment(editor, oldDecl, CreateLeadingCommentTrivia($"Unsupported MethodKind: {method.MethodKind}"));
                        break;
                    }
            }
        }

        private static SyntaxTriviaList CreateLeadingCommentTrivia(string comment)
        {
            var text = comment?.Trim() ?? string.Empty;

            // Ensure it is a line comment (caller can still pass block comment text if they want)
            if (!text.StartsWith("//") && !text.StartsWith("/*"))
                text = "// " + text;

            return SyntaxFactory.TriviaList(
                SyntaxFactory.Comment(text),
                SyntaxFactory.ElasticCarriageReturnLineFeed);
        }

        private void AddCannotConvertComment(DocumentEditor editor, SyntaxNode node, SyntaxTriviaList commentTrivia)
        {
            editor.ReplaceNode(node, WithPrependedLeadingTrivia(node, commentTrivia));
        }

        private static TNode WithPrependedLeadingTrivia<TNode>(TNode node, SyntaxTriviaList trivia)
            where TNode : SyntaxNode
        {
            var existing = node.GetLeadingTrivia();
            return node.WithLeadingTrivia(trivia.AddRange(existing));
        }

        private bool CanConvertMethod(IMethodSymbol method)
        {
            if (method == null)
                return false;

            if (method.DeclaringSyntaxReferences.Length == 0)
                return false;

            if (method.IsExtern)
                return false;

            if (method.MethodKind != MethodKind.Ordinary &&
                method.MethodKind != MethodKind.LocalFunction)
                return false;

            return true;
        }

        private async Task<List<SyntaxNode>> GetMethodDeclarationsAsync(IMethodSymbol method)
        {
            var results = new List<SyntaxNode>();

            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                var node = await syntaxRef.GetSyntaxAsync().ConfigureAwait(false);
                if (node != null)
                    results.Add(node);
            }

            return results;
        }

        private MethodDeclarationSyntax ConvertMethodDeclaration(IMethodSymbol method, MethodDeclarationSyntax methodDecl)
        {
            var newReturnType = ComputeNewReturnType(method, methodDecl.ReturnType);

            var updated = methodDecl.WithReturnType(newReturnType);

            if (methodDecl.Body != null || methodDecl.ExpressionBody != null)
                updated = updated.WithModifiers(AddAsyncModifier(methodDecl.Modifiers));

            return updated;
        }

        private LocalFunctionStatementSyntax ConvertLocalFunctionDeclaration(
            IMethodSymbol method,
            LocalFunctionStatementSyntax localFuncDecl)
        {
            var newReturnType = ComputeNewReturnType(method, localFuncDecl.ReturnType);

            var updated = localFuncDecl.WithReturnType(newReturnType);

            if (localFuncDecl.Body != null || localFuncDecl.ExpressionBody != null)
                updated = updated.WithModifiers(AddAsyncModifier(localFuncDecl.Modifiers));

            return updated;
        }

        private TypeSyntax ComputeNewReturnType(IMethodSymbol method, TypeSyntax originalReturnTypeSyntax)
        {
            if (method.ReturnsVoid)
                return SyntaxFactory.ParseTypeName("Task");

            return SyntaxFactory.GenericName("Task")
                .WithTypeArgumentList(
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(originalReturnTypeSyntax)));
        }

        private SyntaxTokenList AddAsyncModifier(SyntaxTokenList modifiers)
        {
            if (modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                return modifiers;

            return modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        #endregion


        private async Task<Solution> UpdateMethodReferenceSitesToAsync(
            DocumentId documentId,
            IEnumerable<ReferenceSite> refSites,
            Solution solution)
        {
            var document = solution.GetDocument(documentId);
            if (document == null) return solution;

            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root == null) return solution;

            foreach (var refSite in refSites.OrderByDescending(s => s.Span.Start))
            {
                var anchorNode = root.FindNode(refSite.Span, getInnermostNodeForTie: true);

                var invocation =
                    anchorNode.FirstAncestorOrSelf<InvocationExpressionSyntax>()
                    ?? anchorNode.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation == null)
                    continue;

                if (invocation.FirstAncestorOrSelf<AwaitExpressionSyntax>() != null)
                    continue;

                var awaitedInvocation =
                    SyntaxFactory.AwaitExpression(invocation.WithoutTrivia())
                        .WithTriviaFrom(invocation)
                        .WithAdditionalAnnotations(Formatter.Annotation);

                // Replace in *this* root (so the node is guaranteed to be part of the tree)
                root = root.ReplaceNode(invocation, awaitedInvocation);
            }

            var changedDoc = document.WithSyntaxRoot(root);
            changedDoc = await Formatter.FormatAsync(changedDoc).ConfigureAwait(false);

            return changedDoc.Project.Solution;
        }

        private async Task<Solution> WriteCommentNearReferenceSite(string comment, ReferenceSite site, Solution solution)
        {
            var document = solution.GetDocument(site.DocumentId);
            if (document == null)
                return solution;

            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root == null)
                return solution;

            // Anchor on token, then choose a stable “target” to comment:
            // prefer statement; fallback to member declaration; fallback to token parent
            var token = root.FindToken(site.Span.Start);
            var node = token.Parent;

            var target =
                (SyntaxNode?)node?.FirstAncestorOrSelf<StatementSyntax>()
                ?? node?.FirstAncestorOrSelf<MemberDeclarationSyntax>()
                ?? node;

            if (target == null)
                return solution;

            var editor = await DocumentEditor.CreateAsync(document).ConfigureAwait(false);

            var trivia = CreateLeadingCommentTrivia(comment);
            editor.ReplaceNode(target, WithPrependedLeadingTrivia(target, trivia));

            var changedDoc = editor.GetChangedDocument();
            changedDoc = await Formatter.FormatAsync(changedDoc).ConfigureAwait(false);

            return changedDoc.Project.Solution;
        }

        public static async Task<InvocationExpressionSyntax?> TryGetInvocationAsync(
        ReferenceSite site,
        Solution solution)
        {
            var doc = solution.GetDocument(site.DocumentId);
            if (doc == null) return null;

            var root = await doc.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root == null) return null;

            var node = root.FindNode(site.Span, getInnermostNodeForTie: true);
            return node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        }

    }
}
