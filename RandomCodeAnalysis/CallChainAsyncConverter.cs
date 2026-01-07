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
        private readonly List<string> _conversionLog = new List<string>();

        public async Task ConvertMethodChainToAsync(MethodReferenceNode topNode, Solution solution)
        {
            _conversionLog.Clear();
            _conversionLog.Add($"=== Async Conversion Started at {DateTime.Now} ===");
            _conversionLog.Add($"Top-level method: {topNode.MethodName}");
            _conversionLog.Add("");

            var editedSolution = await RecurseCallChain(topNode, solution);

            await RATWriter.SaveSolutionToDiskAsync(editedSolution, solution);

            _conversionLog.Add("");
            _conversionLog.Add($"=== Async Conversion Completed at {DateTime.Now} ===");
            await WriteLogToFileAsync(solution);
        }

        private async Task WriteLogToFileAsync(Solution solution)
        {
            try
            {
                var solutionDir = Path.GetDirectoryName(solution.FilePath);
                if (string.IsNullOrEmpty(solutionDir))
                {
                    solutionDir = Directory.GetCurrentDirectory();
                }

                var logFileName = $"AsyncConversion_{DateTime.Now:yyyyMMdd_HHmmss}.log";
                var logFilePath = Path.Combine(solutionDir, logFileName);

                await File.WriteAllLinesAsync(logFilePath, _conversionLog);
                Console.WriteLine($"Conversion log written to: {logFilePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write conversion log: {ex.Message}");
            }
        }

        private async Task<Solution> RecurseCallChain(MethodReferenceNode node, Solution solution)
        {
            _conversionLog.Add($"Processing method: {node.MethodName}");

            //Convert Method Signatures to Async
            if (node.ReferencedMethod.MethodKind == MethodKind.Ordinary ||
                node.ReferencedMethod.MethodKind == MethodKind.LocalFunction)
            {
                if (node.ReferencedMethod.IsAsync)
                {
                    _conversionLog.Add($"  ✓ Already async, skipping: {node.MethodName}");
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

                _conversionLog.Add($"  ✓ Converted signature to async: {node.MethodName}");

                // rename
                solution = await RenameMethodSymbolToAsync(node.ReferencedMethod, solution);
                _conversionLog.Add($"  ✓ Renamed to: {node.ReferencedMethod.Name}Async");

            }
            else
            {
                //Leave comment
                solution = await WriteCommentNearMethodDecls("Cannot convert: MethodKind is unsupported", node.ReferencedMethod, solution);
                return solution;
            }

            // Convert Call Sites to Await (grouped per doc to avoid span drift)
            var callSiteCount = node.CallSites.Count;
            if (callSiteCount > 0)
            {
                _conversionLog.Add($"  Converting {callSiteCount} call site(s) to await");
                foreach (var group in node.CallSites.GroupBy(cs => cs.DocumentId))
                {
                    solution = await UpdateMethodReferenceSitesToAsync(group.Key, group, solution)
                        .ConfigureAwait(false);
                }
            }

            //Recurse Graph
            if (node.CallerNodes.Count > 0)
            {
                _conversionLog.Add($"  Recursing to {node.CallerNodes.Count} caller(s)");
            }

            foreach (var referenceNode in node.CallerNodes)
            {
                // Refresh the symbol before recursing
                var freshSymbol = await RefreshMethodSymbol(referenceNode.ReferencedMethod, solution);
                if (freshSymbol != null)
                {
                    referenceNode.ReferencedMethod = freshSymbol;
                }
                else
                {
                    var methodLocation = GetMethodLocation(referenceNode.ReferencedMethod);
                    _conversionLog.Add($"  ✗ WARNING: Unable to refresh symbol for caller: {referenceNode.MethodName}");
                    _conversionLog.Add($"    Location: {methodLocation}");
                    _conversionLog.Add($"    This method's call chain may not be properly converted to async.");
                    _conversionLog.Add($"    Skipping recursion for this branch.");
                    continue;
                }

                solution = await RecurseCallChain(referenceNode, solution);
            }

            return solution;
        }

        #region Method Update Logic

        private string GetMethodLocation(IMethodSymbol method)
        {
            try
            {
                var location = method.Locations.FirstOrDefault();
                if (location == null || !location.IsInSource)
                    return "Location unknown";

                var filePath = location.SourceTree?.FilePath ?? "Unknown file";
                var lineSpan = location.GetLineSpan();
                return $"{filePath}:{lineSpan.StartLinePosition.Line + 1}";
            }
            catch
            {
                return "Location unavailable";
            }
        }

        private async Task<IMethodSymbol?> RefreshMethodSymbol(
            IMethodSymbol oldSymbol,
            Solution newSolution)
        {
            // Get the project from new solution
            var project = newSolution.Projects.FirstOrDefault(p =>
                p.AssemblyName == oldSymbol.ContainingAssembly.Name);
            if (project == null) return null;

            var compilation = await project.GetCompilationAsync();
            if (compilation == null) return null;

            // Get the containing type
            var containingTypeFullName = oldSymbol.ContainingType
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "");

            var containingType = compilation.GetTypeByMetadataName(containingTypeFullName);
            if (containingType == null) return null;

            // Find method by signature (to handle overloads)
            var paramSignature = string.Join(",", oldSymbol.Parameters.Select(p =>
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

            return containingType.GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m =>
                    m.Name == oldSymbol.Name &&
                    string.Join(",", m.Parameters.Select(p =>
                        p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))) == paramSignature);
        }

        private async Task<Solution> RenameMethodSymbolToAsync(IMethodSymbol method, Solution solution)
        {
            // Don't double-rename
            if (method.Name.EndsWith("Async", StringComparison.Ordinal))
                return solution;

            var newName = method.Name + "Async";

            var options = new SymbolRenameOptions(
                RenameOverloads: true,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);

            try
            {
                var newSolution = await Renamer.RenameSymbolAsync(
                    solution,
                    method,
                    options,
                    newName,
                    CancellationToken.None);

                return newSolution;
            }
            catch (Exception ex)
            {
                _conversionLog.Add($"  ✗ WARNING: Failed to rename method {method.Name} to {newName}");
                _conversionLog.Add($"    Exception: {ex.Message}");
                return solution;
            }
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

            var semanticModel = await document.GetSemanticModelAsync().ConfigureAwait(false);

            // First pass: collect invocations that should be awaited
            var invocationsToAwait = new List<InvocationExpressionSyntax>();

            _conversionLog.Add($"  Examining {refSites.Count()} reference site(s) in document");

            foreach (var refSite in refSites)
            {
                var anchorNode = root.FindNode(refSite.Span, getInnermostNodeForTie: true);

                var invocation =
                    anchorNode.FirstAncestorOrSelf<InvocationExpressionSyntax>()
                    ?? anchorNode.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocation == null)
                {
                    _conversionLog.Add($"    ⚠ Could not find invocation at span {refSite.Span}");
                    continue;
                }

                // Skip if already awaited
                if (invocation.FirstAncestorOrSelf<AwaitExpressionSyntax>() != null)
                {
                    _conversionLog.Add($"    ⊘ Already awaited: {invocation.ToString().Substring(0, Math.Min(50, invocation.ToString().Length))}...");
                    continue;
                }

                bool shouldAwait = false;
                string reasonSkipped = "";

                // Check if the method being called returns Task (i.e., is actually async)
                if (semanticModel != null)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
                    {
                        var returnType = methodSymbol.ReturnType;

                        // Check if it returns Task or Task<T>
                        if (returnType.Name == "Task" && returnType.ContainingNamespace?.ToString() == "System.Threading.Tasks")
                        {
                            shouldAwait = true;
                        }
                        else
                        {
                            reasonSkipped = $"Return type is {returnType.ToDisplayString()}, not Task";
                        }
                    }
                    else
                    {
                        // Symbol couldn't be resolved - this might be a problem
                        // But we'll default to awaiting since it's in our reference list
                        _conversionLog.Add($"    ⚠ Could not resolve symbol for invocation, will attempt await anyway: {invocation.ToString().Substring(0, Math.Min(50, invocation.ToString().Length))}...");
                        shouldAwait = true;
                    }
                }
                else
                {
                    // No semantic model - default to awaiting
                    _conversionLog.Add($"    ⚠ No semantic model available, will attempt await: {invocation.ToString().Substring(0, Math.Min(50, invocation.ToString().Length))}...");
                    shouldAwait = true;
                }

                if (shouldAwait)
                {
                    invocationsToAwait.Add(invocation);
                }
                else
                {
                    _conversionLog.Add($"    ⊘ Skipping (not Task): {invocation.ToString().Substring(0, Math.Min(50, invocation.ToString().Length))}... - {reasonSkipped}");
                }
            }

            _conversionLog.Add($"  Adding await to {invocationsToAwait.Count} invocation(s)");

            // Second pass: replace all invocations with await (in reverse order to avoid span drift)
            foreach (var invocation in invocationsToAwait.OrderByDescending(inv => inv.SpanStart))
            {
                var awaitedInvocation =
                    SyntaxFactory.AwaitExpression(invocation.WithoutTrivia())
                        .WithTriviaFrom(invocation)
                        .WithAdditionalAnnotations(Formatter.Annotation);

                // Replace in *this* root (so the node is guaranteed to be part of the tree)
                root = root.ReplaceNode(invocation, awaitedInvocation);
            }

            var changedDoc = document.WithSyntaxRoot(root);

            // Add using System.Threading.Tasks if not present
            changedDoc = await EnsureTaskUsingDirective(changedDoc).ConfigureAwait(false);

            changedDoc = await Formatter.FormatAsync(changedDoc).ConfigureAwait(false);

            return changedDoc.Project.Solution;
        }

        private async Task<Document> EnsureTaskUsingDirective(Document document)
        {
            var root = await document.GetSyntaxRootAsync().ConfigureAwait(false);
            if (root is not CompilationUnitSyntax compilationUnit)
                return document;

            // Check if System.Threading.Tasks using is already present
            var hasTaskUsing = compilationUnit.Usings.Any(u =>
                u.Name?.ToString() == "System.Threading.Tasks");

            if (hasTaskUsing)
                return document;

            // Add the using directive
            var taskUsing = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("System.Threading.Tasks"))
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

            var newCompilationUnit = compilationUnit.AddUsings(taskUsing);

            return document.WithSyntaxRoot(newCompilationUnit);
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
