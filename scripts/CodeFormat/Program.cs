using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO.Compression;

// 先验证所有文件，再统一写回；失败时保留原文件，审计记录仅包含路径、计数和哈希。
var root = Path.GetFullPath(args[0]);
var check = args.Contains("--check");
var baselineIndex = Array.IndexOf(args, "--baseline");
using var baseline = baselineIndex >= 0
    ? ZipFile.OpenRead(args[baselineIndex + 1])
    : null;
var pending = new List<(string Path, string Text)>();
var reports = new List<object>();
foreach (var file in new[]
{
    "src",
    "tests",
    "scripts"
}.SelectMany(
    folder => Directory.EnumerateFiles(
        Path.Combine(root, folder),
        "*.cs",
        SearchOption.AllDirectories
    )
)
    .Where(
        file => !Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar)
            .Any(
                p => p is "bin" or "obj"
            )
    )
    .Order())
{
    var original = File.ReadAllText(file);
    var syntax = Parse(original);
    var output = NestedLayout.Format(syntax)
        .ToFullString()
        .TrimEnd() + "\n";
    var reparsed = Parse(output);
    // 可选地对照已经交付的源码包，直接证明本轮没有改变产品或测试的语法内容。
    var relative = Path.GetRelativePath(root, file);
    var baselineEntry = relative.StartsWith("src/") || relative.StartsWith("tests/")
        ? baseline?.GetEntry("pcl-mac-port/" + relative)
        : null;
    if (baselineEntry is not null)
    {
        using var reader = new StreamReader(
            baselineEntry.Open()
        );
        var released = Parse(
            reader.ReadToEnd()
        );
        if (!released.DescendantTokens()
            .Select(
                t => (t.RawKind, t.Text)
            )
            .SequenceEqual(
                reparsed.DescendantTokens()
                    .Select(
                        t => (t.RawKind, t.Text)
                    )
            ) || !MeaningfulTrivia(released)
            .SequenceEqual(
                MeaningfulTrivia(reparsed)
            ))
            throw new InvalidDataException("与历史交付源码的内容不一致：" + relative);
    }

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
    {
        throw new InvalidDataException("语法 token 改变：" + file);
    }

    if (!MeaningfulTrivia(syntax)
        .SequenceEqual(
            MeaningfulTrivia(reparsed)
        ))
    {
        throw new InvalidDataException("注释或指令改变：" + file);
    }

    if (NestedLayout.Format(reparsed)
        .ToFullString()
        .TrimEnd() + "\n" != output)
    {
        throw new InvalidDataException("格式化不满足幂等性：" + file);
    }

    if (original != output)
    {
        pending.Add((file, output));
    }

    reports.Add(
        new
        {
            file = Path.GetRelativePath(root, file),
            changed = original != output,
            tokenEquivalent = true,
            releaseBaselineEquivalent = baselineEntry is not null
                ? (bool? )true
                : null,
            commentsAndDirectivesEquivalent = true,
            idempotent = true,
            beforeSha256 = Hash(original),
            afterSha256 = Hash(output)
        }
    );
}

if (check)
{
    Console.WriteLine($"Checked {reports.Count} files; {pending.Count} require formatting.");
    Environment.ExitCode = pending.Count == 0
        ? 0
        : 1;
}
else
{
    foreach (var(path, text)in pending)
    {
        File.WriteAllText(path, text);
    }

    var directory = Path.Combine(root, "artifacts/formatting-current");
    Directory.CreateDirectory(directory);
    File.WriteAllText(
        Path.Combine(directory, "csharp-audit.json"),
        JsonSerializer.Serialize(
            reports,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
    Console.WriteLine(
        $"Formatted {pending.Count} of {reports.Count} files; tokens, comments, directives and idempotence verified."
    );
}

// 预处理禁用文本也作为非空白 trivia 校验；无法解析的源文件不进入写回阶段。
static SyntaxNode Parse(string text)
{
    var tree = CSharpSyntaxTree.ParseText(
        text,
        new CSharpParseOptions(LanguageVersion.Preview)
    );
    if (tree.GetDiagnostics()
        .Any(
            d => d.Severity == DiagnosticSeverity.Error
        ))
    {
        throw new InvalidDataException(
            string.Join(
                "\n",
                tree.GetDiagnostics()
            )
        );
    }

    return tree.GetRoot();
}

static IEnumerable<(int, string)> MeaningfulTrivia(SyntaxNode node) => node.DescendantTrivia()
    .Where(
        t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)
    )
    .Select(
        t => (t.RawKind, t.ToFullString())
    );
static string Hash(string text) => Convert.ToHexString(
    SHA256.HashData(
        Encoding.UTF8.GetBytes(text)
    )
)
    .ToLowerInvariant();
