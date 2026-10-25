using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

/// <summary>在语法边界展开嵌套结构，再按语法节点的层级使用四空格缩进；不触碰字符串或注释内容。</summary>
internal static class NestedLayout
{
    public static SyntaxNode Format(SyntaxNode root)
    {
        root = root.NormalizeWhitespace("    ", "\n", elasticTrivia: false);
        var before = new HashSet<SyntaxToken>();
        var after = new HashSet<SyntaxToken>();
        // 成对边界与分隔符分别换行，内层初始化器和调用自然再增加一层缩进。
        void Expand(SyntaxToken open, SyntaxToken close, IEnumerable<SyntaxToken> separators)
        {
            after.Add(open);
            before.Add(close);
            foreach (var separator in separators)
            {
                after.Add(separator);
            }
        }

        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case InitializerExpressionSyntax init when init.Expressions.Count > 0:
                    before.Add(init.OpenBraceToken);
                    Expand(
                        init.OpenBraceToken,
                        init.CloseBraceToken,
                        init.Expressions.GetSeparators()
                    );
                    break;
                case AnonymousObjectCreationExpressionSyntax anonymous when anonymous.Initializers.Count > 0:
                    before.Add(anonymous.OpenBraceToken);
                    Expand(
                        anonymous.OpenBraceToken,
                        anonymous.CloseBraceToken,
                        anonymous.Initializers.GetSeparators()
                    );
                    break;
                case CollectionExpressionSyntax collection when collection.Elements.Count > 0:
                    Expand(
                        collection.OpenBracketToken,
                        collection.CloseBracketToken,
                        collection.Elements.GetSeparators()
                    );
                    break;
                case SwitchExpressionSyntax expression:
                    before.Add(expression.OpenBraceToken);
                    Expand(
                        expression.OpenBraceToken,
                        expression.CloseBraceToken,
                        expression.Arms.GetSeparators()
                    );
                    break;
                case ArgumentListSyntax arguments when arguments.Arguments.Count > 0 && (CodeWidth(arguments) > 100 || arguments.Arguments.Any(
                    a => a.DescendantNodes()
                        .Any(IsNested)
                )):
                    Expand(
                        arguments.OpenParenToken,
                        arguments.CloseParenToken,
                        arguments.Arguments.GetSeparators()
                    );
                    break;
                case ParameterListSyntax parameters when parameters.Parameters.Count > 0 && CodeWidth(parameters) > 100:
                    Expand(
                        parameters.OpenParenToken,
                        parameters.CloseParenToken,
                        parameters.Parameters.GetSeparators()
                    );
                    break;
                case ConditionalExpressionSyntax conditional:
                    before.Add(conditional.QuestionToken);
                    before.Add(conditional.ColonToken);
                    break;
                case MemberAccessExpressionSyntax member when member.Expression is InvocationExpressionSyntax:
                    before.Add(member.OperatorToken);
                    break;
            }
        }

        // 只新增换行 trivia。保留注释、预处理指令和已有换行，避免改变注释覆盖的代码范围。
        var expanded = root.ReplaceTokens(
            before.Union(after),
            (original, rewritten) =>
            {
                if (before.Contains(original) && !original.GetPreviousToken()
                    .TrailingTrivia.Concat(rewritten.LeadingTrivia)
                    .Any(
                        t => t.IsKind(SyntaxKind.EndOfLineTrivia)
                    ))
                {
                    rewritten = rewritten.WithLeadingTrivia(
                        rewritten.LeadingTrivia.Insert(
                            0,
                            SyntaxFactory.EndOfLine("\n")
                        )
                    );
                }

                if (after.Contains(original) && !rewritten.TrailingTrivia.Concat(
                    original.GetNextToken()
                        .LeadingTrivia
                )
                    .Any(
                        t => t.IsKind(SyntaxKind.EndOfLineTrivia)
                    ))
                {
                    rewritten = rewritten.WithTrailingTrivia(
                        rewritten.TrailingTrivia.Add(
                            SyntaxFactory.EndOfLine("\n")
                        )
                    );
                }

                return rewritten;
            }
        );
        var reparsed = CSharpSyntaxTree.ParseText(
            expanded.ToFullString(),
            new CSharpParseOptions(LanguageVersion.Preview)
        )
            .GetRoot();
        return Indent(reparsed);
    }

    // 单纯常量参数可同行；调用中再出现调用、初始化器或分支时必须逐层展开。
    private static bool IsNested(SyntaxNode node) => node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or InitializerExpressionSyntax or AnonymousObjectCreationExpressionSyntax or CollectionExpressionSyntax or ConditionalExpressionSyntax or LambdaExpressionSyntax;
    // 以 token 长度判断宽度，避免第一次加入缩进后第二次又触发额外换行。
    private static int CodeWidth(SyntaxNode node) => node.DescendantTokens()
        .Sum(
            t => t.Text.Length + 1
        );
    /// <summary>用语法范围计算缩进；字面量和多行注释内部的行保持原样。</summary>
    private static SyntaxNode Indent(SyntaxNode root)
    {
        var source = root.SyntaxTree.GetText();
        var scopes = new List<TextSpan>();
        void Scope(int start, int end)
        {
            if (end > start)
                scopes.Add(
                    TextSpan.FromBounds(start, end)
                );
        }

        foreach (var node in root.DescendantNodesAndSelf())
        {
            // 括号范围不包含右括号本身，闭合行会自然回退一级。
            var braces = node.ChildTokens()
                .Where(
                    t => t.IsKind(SyntaxKind.OpenBraceToken) || t.IsKind(SyntaxKind.CloseBraceToken)
                )
                .ToArray();
            if (braces.Length == 2)
                Scope(braces[0].Span.End, braces[1].SpanStart);
            switch (node)
            {
                case ArgumentListSyntax args when source.Lines.GetLineFromPosition(args.OpenParenToken.SpanStart)
                    .LineNumber != source.Lines.GetLineFromPosition(args.CloseParenToken.SpanStart)
                    .LineNumber:
                    Scope(args.OpenParenToken.Span.End, args.CloseParenToken.SpanStart);
                    break;
                case ParameterListSyntax args when source.Lines.GetLineFromPosition(args.OpenParenToken.SpanStart)
                    .LineNumber != source.Lines.GetLineFromPosition(args.CloseParenToken.SpanStart)
                    .LineNumber:
                    Scope(args.OpenParenToken.Span.End, args.CloseParenToken.SpanStart);
                    break;
                case CollectionExpressionSyntax collection:
                    Scope(collection.OpenBracketToken.Span.End, collection.CloseBracketToken.SpanStart);
                    break;
                case MemberAccessExpressionSyntax member when member.Expression is InvocationExpressionSyntax:
                    Scope(
                        member.OperatorToken.SpanStart,
                        member.Parent is InvocationExpressionSyntax call
                            ? call.Span.End
                            : member.Span.End
                    );
                    break;
                case ConditionalExpressionSyntax conditional:
                    Scope(conditional.QuestionToken.SpanStart, conditional.WhenFalse.Span.End);
                    break;
                case SwitchSectionSyntax section when section.Statements.Count > 0:
                    Scope(section.Statements[0].FullSpan.Start, section.Span.End);
                    break;
                case StatementSyntax statement when statement is not BlockSyntax && !(statement is IfStatementSyntax && statement.Parent is ElseClauseSyntax) && (statement.Parent is IfStatementSyntax or ElseClauseSyntax or ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax or UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax):
                    Scope(statement.FullSpan.Start, statement.Span.End);
                    break;
            }
        }

        var changes = new List<TextChange>();
        foreach (var line in source.Lines)
        {
            var text = line.ToString();
            var count = text.TakeWhile(
                c => c is ' ' or '\t'
            )
                .Count();
            if (count == text.Length)
                continue;
            var position = line.Start + count;
            var token = root.FindToken(position);
            // 原始字符串、插值文本、注释中的换行不是代码排版边界。
            if (token.Span.Contains(position) && position > token.SpanStart)
                continue;
            var trivia = root.FindTrivia(position);
            if (trivia.RawKind != 0 && trivia.Span.Contains(position) && position > trivia.SpanStart)
                continue;
            var depth = scopes.Count(
                s => s.Contains(position)
            );
            var trailing = text.Length - text.TrimEnd(' ', '\t')
                .Length;
            if (trailing > 0 && root.FindTrivia(line.End - 1)
                .IsKind(SyntaxKind.WhitespaceTrivia))
                changes.Add(
                    new TextChange(
                        new TextSpan(line.End - trailing, trailing),
                        ""
                    )
                );
            var indentation = new string (' ', depth * 4);
            if (text[..count] != indentation)
                changes.Add(
                    new TextChange(
                        new TextSpan(line.Start, count),
                        indentation
                    )
                );
        }

        return CSharpSyntaxTree.ParseText(
            source.WithChanges(changes),
            new CSharpParseOptions(LanguageVersion.Preview)
        )
            .GetRoot();
    }
}
