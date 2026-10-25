using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 对自有源码补充结构注释。Roslyn 只改 trivia；逐 token 核对保证不会修改表达式、字符串或指令。
var root = Path.GetFullPath(args[0]);
var domains = JsonSerializer.Deserialize<Dictionary<string, string>>(
    File.ReadAllText(
        Path.Combine(root, "scripts/code-comment-domains.json")
    )
)!;
var summaries = JsonSerializer.Deserialize<Dictionary<string, string>>(
    File.ReadAllText(
        Path.Combine(root, "scripts/code-comment-overrides.json")
    )
)!;
bool HasComment(SyntaxTriviaList trivia) => trivia.Any(
    t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
);
var reports = new List<object>();
foreach (var file in Directory.EnumerateFiles(
    Path.Combine(root, "src"),
    "*.cs",
    SearchOption.AllDirectories
)
    .Where(
        x => !x.Split(Path.DirectorySeparatorChar)
            .Any(
                p => p is "bin" or "obj"
            )
    )
    .Order())
{
    var original = File.ReadAllText(file);
    var tree = CSharpSyntaxTree.ParseText(
        original,
        new CSharpParseOptions(LanguageVersion.Preview)
    );
    var syntax = tree.GetRoot();
    var name = Path.GetFileNameWithoutExtension(file);
    if (!domains.TryGetValue(name, out var domain))
        throw new Exception("缺少人工编写的模块说明：" + name);
    var clean = new GeneratedCommentCleaner().Visit(syntax)!;
    var rewriter = new Comments(domain, name, summaries);
    var annotated = NestedLayout.Format(
        rewriter.Visit(clean)!.NormalizeWhitespace("    ", "\n")
    );
    // 拒绝任何语法 token 的变动，注释本身不应改变应用行为。
    if (!syntax.DescendantTokens()
        .Select(
            t => (t.RawKind, t.Text)
        )
        .SequenceEqual(
            annotated.DescendantTokens()
                .Select(
                    t => (t.RawKind, t.Text)
                )
        ))
        throw new Exception("Token changed: " + file);
    var output = annotated.ToFullString();
    var reparsed = CSharpSyntaxTree.ParseText(
        output,
        new CSharpParseOptions(LanguageVersion.Preview)
    )
        .GetRoot();
    if (!syntax.DescendantTokens()
        .Select(
            t => (t.RawKind, t.Text)
        )
        .SequenceEqual(
            reparsed.DescendantTokens()
                .Select(
                    t => (t.RawKind, t.Text)
                )
        ))
        throw new Exception("Reparse changed: " + file);
    // 保持原有排版；只在实际新增了注释的文件写回。
    if (output != original)
        File.WriteAllText(file, output);
    reports.Add(
        new
        {
            file = Path.GetRelativePath(root, file),
            rewriter.Added,
            tokenEquivalent = true,
            blocks = reparsed.DescendantNodes()
                .OfType<BlockSyntax>()
                .Count(),
            uncommentedBlocks = reparsed.DescendantNodes()
                .OfType<BlockSyntax>()
                .Where(
                    b => !HasComment(
                        b.Parent is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                            ? b.Parent.GetLeadingTrivia()
                            : b.Statements.Count > 0
                                ? b.Statements[0].GetLeadingTrivia()
                                : b.OpenBraceToken.TrailingTrivia.AddRange(b.CloseBraceToken.LeadingTrivia)
                    )
                )
                .Select(
                    b => b.GetLocation()
                        .GetLineSpan()
                        .StartLinePosition.Line + 1
                )
                .ToArray(),
            uncommentedDeclarations = reparsed.DescendantNodes()
                .Where(
                    x => x is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                )
                .Where(
                    x => !HasComment(
                        x.GetLeadingTrivia()
                    )
                )
                .Select(
                    x => x.GetLocation()
                        .GetLineSpan()
                        .StartLinePosition.Line + 1
                )
                .ToArray(),
            declarations = reparsed.DescendantNodes()
                .Count(
                    x => x is BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                )
        }
    );
}

Directory.CreateDirectory(
    Path.Combine(root, "artifacts/comment-current")
);
File.WriteAllText(
    Path.Combine(root, "artifacts/comment-current/comment-token-audit.json"),
    JsonSerializer.Serialize(
        reports,
        new JsonSerializerOptions
        {
            WriteIndented = true
        }
    )
);
Console.WriteLine($"Annotated {reports.Count} source files; token equality verified.");
/// <summary>人工模块用途与语法树中的实际操作共同组成注释，已有注释不被覆盖。</summary>
sealed class Comments(string domain, string module, Dictionary<string, string> summaries) : CSharpSyntaxRewriter
{
    public int Added;
    static bool Commented(SyntaxTriviaList trivia) => trivia.Any(
        t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia) || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
    );
    static string Short(SyntaxNode? node, int max = 95)
    {
        if (node is null)
            return "空";
        var text = Regex.Replace(
            node.WithoutTrivia()
                .ToString(),
            @"\s+",
            " "
        )
            .Replace("*/", "* /");
        return text.Length > max
            ? text[..max] + "…"
            : text;
    }

    static string Name(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text + " 构造",
        LocalFunctionStatementSyntax f => f.Identifier.Text,
        DestructorDeclarationSyntax d => d.Identifier.Text + " 释放",
        _ => node.Kind()
            .ToString()
    };
    // 将本段前几个语句映射成具体的数据流说明，避免凭空推断网络成功、授权或游戏运行。
    static string Steps(IEnumerable<StatementSyntax> statements) => string.Join(
        "；",
        statements.Take(3)
            .Select(
                s => s switch
                {
                    LocalDeclarationStatementSyntax l => "准备 " + Short(l.Declaration),
                    IfStatementSyntax i => "按 " + Short(i.Condition) + " 分支处理",
                    ForEachStatementSyntax f => "遍历 " + Short(f.Expression) + " 处理 " + f.Identifier.Text,
                    ForStatementSyntax f => "在 " + Short(f.Condition) + " 条件下迭代",
                    WhileStatementSyntax w => "在 " + Short(w.Condition) + " 条件下循环",
                    ReturnStatementSyntax r => r.Expression is null
                        ? "结束当前过程"
                        : "返回 " + Short(r.Expression),
                    ThrowStatementSyntax t => t.Expression is null
                        ? "重新抛出当前异常"
                        : "抛出 " + Short(t.Expression),
                    TryStatementSyntax => "执行受异常处理保护的操作",
                    UsingStatementSyntax u => "在作用域内使用并释放 " + Short((SyntaxNode? )u.Declaration ?? u.Expression),
                    LockStatementSyntax l => "锁定 " + Short(l.Expression) + " 后访问共享状态",
                    ExpressionStatementSyntax e => "执行 " + Short(e.Expression),
                    _ => "执行 " + Short(s)
                }
            )
    );
    T Prefix<T>(T node, string message)
    where T : SyntaxNode
    {
        if (Commented(
            node.GetLeadingTrivia()
        ))
            return node;
        Added++;
        // 使用换行边界隔开单行注释；原节点的 trivia 仍完整保留。
        return node.WithLeadingTrivia(
            SyntaxFactory.ParseLeadingTrivia(
                "\n// [实现] " + message.Replace("\n", " ") + "\n"
            )
                .AddRange(
                    node.GetLeadingTrivia()
                )
        );
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => Prefix(
        (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!,
        domain + "。"
    );
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Prefix(
        (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!,
        "接口契约：" + node.Identifier.Text + " 声明模块依赖的操作，由具体实现负责存储或系统交互。"
    );
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => Prefix(
        (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!,
        "数据约定：" + node.Identifier.Text + " 汇集 " + string.Join(
            "、",
            node.ParameterList?.Parameters.Select(
                x => x.Identifier.Text
            )
                .Take(12) ?? []
        ) + "，供该模块传递状态。"
    );
    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) => Prefix(
        (EnumDeclarationSyntax)base.VisitEnumDeclaration(node)!,
        "状态集合：" + node.Identifier.Text + " 限定为 " + string.Join(
            "、",
            node.Members.Select(
                x => x.Identifier.Text
            )
        ) + "。"
    );
    SyntaxNode Method(
        SyntaxNode before,
        SyntaxNode after,
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? expression
    ) => Prefix(
        after,
        summaries.TryGetValue(
            module + "." + Name(before),
            out var summary
        )
            ? summary
            : Name(before) + " 的实现：" + (body is not null
                ? Steps(body.Statements)
                : (expression is null
                    ? "仅声明调用契约，由实现类提供行为"
                    : (before is MethodDeclarationSyntax m && m.ReturnType.ToString() == "void" || before is LocalFunctionStatementSyntax f && f.ReturnType.ToString() == "void"
                        ? "执行 "
                        : "计算并返回 ") + Short(expression.Expression))) + "。"
    );
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => Method(
        node,
        base.VisitMethodDeclaration(node)!,
        node.Body,
        node.ExpressionBody
    );
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => Method(
        node,
        base.VisitConstructorDeclaration(node)!,
        node.Body,
        node.ExpressionBody
    );
    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => Method(
        node,
        base.VisitLocalFunctionStatement(node)!,
        node.Body,
        node.ExpressionBody
    );
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        if (node.Parent is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax)
            return visited;
        if (visited.Statements.Count == 0)
        {
            if (!Commented(visited.OpenBraceToken.TrailingTrivia))
            {
                Added++;
                return visited.WithCloseBraceToken(
                    visited.CloseBraceToken.WithLeadingTrivia(
                        SyntaxFactory.ParseLeadingTrivia("\n// [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。\n")
                    )
                );
            }

            return visited;
        }

        if (Commented(visited.OpenBraceToken.TrailingTrivia) || Commented(
            visited.Statements[0].GetLeadingTrivia()
        ))
            return visited;
        var context = node.Parent switch
        {
            IfStatementSyntax i => "条件成立（" + Short(i.Condition) + "）时",
            ElseClauseSyntax => "前一条件不成立时",
            ForEachStatementSyntax f => "对 " + Short(f.Expression) + " 中的每一项",
            ForStatementSyntax f => "每次迭代（" + Short(f.Condition) + "）",
            WhileStatementSyntax w => "循环满足 " + Short(w.Condition) + " 时",
            CatchClauseSyntax c => "捕获 " + Short(c.Declaration) + " 后",
            FinallyClauseSyntax => "无论成功或失败，退出前",
            AnonymousFunctionExpressionSyntax => "回调触发时",
            TryStatementSyntax => "在异常保护范围内",
            _ => "本段流程"
        };
        var first = Prefix(
            visited.Statements[0],
            context + "：" + Steps(node.Statements) + "。"
        );
        return visited.WithStatements(
            visited.Statements.Replace(visited.Statements[0], first)
        );
    }
}

// 仅刷新本工具生成的结构注释；手写算法说明、许可和来源说明完整保留。
sealed class GeneratedCommentCleaner : CSharpSyntaxRewriter
{
    public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia)
    {
        if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            return base.VisitTrivia(trivia);
        var text = trivia.ToString();
        if (text.StartsWith("// [实现]") || text.Contains(" 的实现：") || new[]
        {
            "// 本段流程",
            "// 数据约定",
            "// 状态集合",
            "// 条件成立（",
            "// 前一条件不成立时",
            "// 对 ",
            "// 每次迭代（",
            "// 循环满足 ",
            "// 捕获 ",
            "// 无论成功或失败，退出前",
            "// 回调触发时",
            "// 在异常保护范围内"
        }.Any(text.StartsWith) && text.Contains("："))
            return default;
        return base.VisitTrivia(trivia);
    }
}
