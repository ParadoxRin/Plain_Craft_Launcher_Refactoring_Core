using Microsoft.CodeAnalysis.CSharp;
// 仅格式化明确列出的自有源码；不运行 token 比较、回归、幂等或审计流程。
foreach (var file in args)
{
    var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    File.WriteAllText(file, NestedLayout.Format(root).ToFullString().TrimEnd() + "\n");
}
