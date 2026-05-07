using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GatewayCallGraph;

/// <summary>
/// Decides whether a call site is enclosed in an if/else/ternary, similar in
/// shape to <see cref="LoopDetector"/>. Innermost conditional wins — that's the
/// most specific signal for "this call only happens when X."
///
/// Stops climbing at the enclosing method/lambda body so we don't escape into
/// callers. Loops are NOT a stop — a call inside <c>foreach { if (x) Foo(); }</c>
/// gets BOTH a Loop and a Conditional annotation on its edge, which is the
/// behavior we want.
///
/// Switch statements are intentionally left out for v1; their condition is a
/// case label, not a boolean expression, and pattern matching makes a clean
/// representation harder. Easy to add later if needed.
/// </summary>
public static class ConditionalDetector
{
    /// <summary>Truncate the rendered condition to keep edge labels readable.</summary>
    private const int MaxConditionLength = 60;

    public static ConditionalInfo? FindEnclosingConditional(SyntaxNode callSite)
    {
        SyntaxNode? current = callSite;
        SyntaxNode? prev = null;

        while (current != null)
        {
            // Stop at the enclosing method/ctor/local-function body boundary.
            // Lambdas and anonymous functions are NOT method boundaries because
            // the call really executes inside whatever invocation receives the
            // lambda — same rule as LoopDetector.
            if (current is MethodDeclarationSyntax ||
                current is ConstructorDeclarationSyntax ||
                current is DestructorDeclarationSyntax ||
                current is OperatorDeclarationSyntax ||
                current is ConversionOperatorDeclarationSyntax ||
                current is AccessorDeclarationSyntax ||
                current is LocalFunctionStatementSyntax)
            {
                return null;
            }

            // if / else if — IfStatementSyntax. We need to know whether `prev`
            // (the child we ascended from) was the Then-branch or the Else-branch
            // so we can flag isElseBranch correctly.
            if (current is IfStatementSyntax ifStmt)
            {
                var isElse = prev != null && ifStmt.Else != null && prev == ifStmt.Else;
                return new ConditionalInfo
                {
                    Kind = isElse ? ConditionalKind.Else : ConditionalKind.If,
                    Condition = TruncateCondition(ifStmt.Condition.ToString()),
                    File = ifStmt.IfKeyword.GetLocation().GetLineSpan().Path,
                    Line = ifStmt.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsElseBranch = isElse,
                };
            }

            // `else` clause without an explicit `if` (e.g. `else { ... }` after
            // an else-if chain). The ElseClauseSyntax wraps either an
            // IfStatementSyntax (for "else if") which we'd already have caught
            // one frame up, or a Block. Treat the latter as Else of the parent if.
            if (current is ElseClauseSyntax elseClause &&
                elseClause.Statement is not IfStatementSyntax)
            {
                if (elseClause.Parent is IfStatementSyntax parentIf)
                {
                    return new ConditionalInfo
                    {
                        Kind = ConditionalKind.Else,
                        Condition = TruncateCondition(parentIf.Condition.ToString()),
                        File = elseClause.ElseKeyword.GetLocation().GetLineSpan().Path,
                        Line = elseClause.ElseKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        IsElseBranch = true,
                    };
                }
            }

            // Ternary — `cond ? Foo() : Bar()`. Whether `prev` was WhenTrue or
            // WhenFalse tells us which branch the call lives in.
            if (current is ConditionalExpressionSyntax ternary)
            {
                var isElse = prev != null && prev == ternary.WhenFalse;
                return new ConditionalInfo
                {
                    Kind = ConditionalKind.Ternary,
                    Condition = TruncateCondition(ternary.Condition.ToString()),
                    File = ternary.QuestionToken.GetLocation().GetLineSpan().Path,
                    Line = ternary.QuestionToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    IsElseBranch = isElse,
                };
            }

            prev = current;
            current = current.Parent;
        }

        return null;
    }

    private static string TruncateCondition(string raw)
    {
        // Collapse newlines + tabs + runs of spaces so multi-line conditions
        // fit on a single edge label. Keep parens/operators verbatim.
        var normalized = string.Join(" ", raw.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        normalized = string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= MaxConditionLength) return normalized;
        return normalized[..(MaxConditionLength - 1)] + "…";
    }
}
