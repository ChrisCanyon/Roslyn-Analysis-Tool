using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GatewayCallGraph;

/// <summary>
/// Determines whether a call site is enclosed in a loop, where "loop" means:
///  - a for / foreach / while / do statement (statement_loop), OR
///  - a lambda/anonymous-function passed to a recognized System.Linq enumerable method
///    such as Select, Where, ForEach, etc. (enumerable_loop).
///
/// Walks up syntax parents from the call site, stopping at the enclosing method-body
/// boundary. Closest enclosing loop wins.
/// </summary>
public static class LoopDetector
{
    /// <summary>
    /// LINQ-ish enumerable methods whose lambda arguments are invoked once per element
    /// (so any call inside that lambda is effectively in a loop at runtime).
    /// We match by simple name on System.Linq.Enumerable / System.Linq.Queryable
    /// plus List&lt;T&gt;.ForEach.
    /// </summary>
    private static readonly HashSet<string> EnumerableLoopMethods = new(StringComparer.Ordinal)
    {
        "Select", "SelectMany", "Where", "ForEach",
        "Sum", "Any", "All", "Count", "LongCount",
        "Aggregate", "GroupBy", "GroupJoin", "Join",
        "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "ToDictionary", "ToLookup", "ToList", "ToArray", "ToHashSet",
        "Min", "Max", "MinBy", "MaxBy", "Average",
        "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Last", "LastOrDefault",
        "Distinct", "DistinctBy", "TakeWhile", "SkipWhile",
    };

    public static LoopInfo? FindEnclosingLoop(SyntaxNode callSite, SemanticModel semanticModel)
    {
        SyntaxNode? current = callSite.Parent;

        // We track:
        //   - the *innermost* loop (its Kind, Subkind, file, line) — that's what
        //     gets rendered as the edge label, since the immediate context is
        //     what's most actionable
        //   - the total nesting depth (count of loops we walk past on the way
        //     to the method body) — used by the side panel to compute call-count
        //     formulas like N^2
        LoopInfo? innermost = null;
        int depth = 0;

        while (current != null)
        {
            // Stop at the enclosing method/ctor/local-function body boundary.
            // Lambdas and anonymous functions are NOT method boundaries for our purposes
            // because the call really does execute inside whatever invocation receives the lambda.
            if (current is MethodDeclarationSyntax ||
                current is ConstructorDeclarationSyntax ||
                current is DestructorDeclarationSyntax ||
                current is OperatorDeclarationSyntax ||
                current is ConversionOperatorDeclarationSyntax ||
                current is AccessorDeclarationSyntax ||
                current is LocalFunctionStatementSyntax)
            {
                break;
            }

            LoopInfo? hit = null;
            switch (current)
            {
                case ForStatementSyntax @for:
                    // for(;;) has no meaningful source; otherwise show the
                    // condition text so the reader sees the termination check.
                    hit = Build(LoopKind.StatementLoop, "for", @for.ForKeyword,
                        source: TruncateExpr(@for.Condition?.ToString()));
                    break;
                case ForEachStatementSyntax @foreach:
                    // For "foreach (var x in accounts)" we surface "accounts" —
                    // the enumerable expression. That's almost always what the
                    // reader wants to see for diagnosing N² hot paths.
                    hit = Build(LoopKind.StatementLoop, "foreach", @foreach.ForEachKeyword,
                        source: TruncateExpr(@foreach.Expression.ToString()));
                    break;
                case ForEachVariableStatementSyntax foreachVar:
                    hit = Build(LoopKind.StatementLoop, "foreach", foreachVar.ForEachKeyword,
                        source: TruncateExpr(foreachVar.Expression.ToString()));
                    break;
                case WhileStatementSyntax @while:
                    hit = Build(LoopKind.StatementLoop, "while", @while.WhileKeyword,
                        source: TruncateExpr(@while.Condition.ToString()));
                    break;
                case DoStatementSyntax @do:
                    hit = Build(LoopKind.StatementLoop, "do", @do.DoKeyword,
                        source: TruncateExpr(@do.Condition.ToString()));
                    break;
            }

            // Lambda / anonymous-function inside a LINQ-enumerable invocation?
            if (hit == null && current is AnonymousFunctionExpressionSyntax anon)
            {
                var enclosingInvocation = anon.Parent;
                // Lambda usually sits inside an Argument -> ArgumentList -> InvocationExpression
                while (enclosingInvocation != null && enclosingInvocation is not InvocationExpressionSyntax)
                {
                    if (enclosingInvocation is MethodDeclarationSyntax) { enclosingInvocation = null; break; }
                    enclosingInvocation = enclosingInvocation.Parent;
                }
                if (enclosingInvocation is InvocationExpressionSyntax invocation)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                                       ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (methodSymbol != null && IsEnumerableLoopMethod(methodSymbol))
                    {
                        var token = invocation.Expression switch
                        {
                            MemberAccessExpressionSyntax m => m.Name.Identifier,
                            _ => invocation.GetFirstToken(),
                        };
                        // For "accounts.Select(a => Foo(a))" the receiver
                        // (accounts) is the enumerable. For static-form LINQ
                        // ("Enumerable.Select(accounts, a => ...)") the first
                        // argument plays the same role.
                        var source = invocation.Expression switch
                        {
                            MemberAccessExpressionSyntax m => TruncateExpr(m.Expression.ToString()),
                            _ => TruncateExpr(invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString()),
                        };
                        hit = Build(LoopKind.EnumerableLoop, methodSymbol.Name, token, source: source);
                    }
                }
            }

            if (hit != null)
            {
                depth++;
                innermost ??= hit; // first one we see is the innermost
            }

            current = current.Parent;
        }

        if (innermost == null) return null;
        return innermost with { Depth = depth };
    }

    private static bool IsEnumerableLoopMethod(IMethodSymbol method)
    {
        if (!EnumerableLoopMethods.Contains(method.Name)) return false;

        // List<T>.ForEach is an instance method on List<T>; everything else we recognize
        // is on System.Linq.Enumerable / System.Linq.Queryable as extension methods.
        var containingType = method.ContainingType;
        if (containingType == null) return false;

        var typeName = containingType.ToDisplayString();
        if (typeName == "System.Linq.Enumerable") return true;
        if (typeName == "System.Linq.Queryable") return true;
        if (typeName.StartsWith("System.Collections.Generic.List<") && method.Name == "ForEach") return true;

        // Some codebases have their own enumerable extension classes that mirror LINQ.
        // For now, only treat well-known LINQ types as enumerable loops to avoid false positives.
        return false;
    }

    private static LoopInfo Build(LoopKind kind, string subkind, Microsoft.CodeAnalysis.SyntaxToken token, string? source = null)
    {
        var location = token.GetLocation();
        var span = location.GetLineSpan();
        return new LoopInfo
        {
            Kind = kind,
            Subkind = subkind,
            File = span.Path,
            Line = span.StartLinePosition.Line + 1,
            Source = source,
        };
    }

    /// <summary>
    /// Whitespace-normalize and truncate an expression for use in a loop label.
    /// Newlines and multi-space runs collapse to single spaces. Anything past
    /// 60 chars gets an ellipsis appended. Returns null for empty input so
    /// downstream consumers can treat "no source" uniformly.
    /// </summary>
    private const int SourceMaxLength = 60;
    private static string? TruncateExpr(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Collapse internal whitespace so multi-line LINQ chains read as one
        // line in the label. Trim outer whitespace.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length <= SourceMaxLength
            ? collapsed
            : collapsed[..SourceMaxLength] + "…";
    }
}
