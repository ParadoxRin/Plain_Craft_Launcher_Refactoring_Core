using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>
/// 读取实例自带的 Quilt schema 1 依赖覆盖配置。只支持精确 id 选择器，
/// 在求解前复制并改写候选依赖；不会修改模组 JAR，也不会执行加载器插件。
/// </summary>
public sealed class QuiltDependencyOverrides
{
    public const string RelativePath = "config/quilt-loader-overrides.json";
    // [实现] 数据约定：Changes 汇集 Replacements、Removals、Additions，供该模块传递状态。
    private sealed record Changes(
        IReadOnlyList<(JToken From, JToken To)> Replacements,
        IReadOnlyList<JToken> Removals,
        IReadOnlyList<JToken> Additions
    );
    // [实现] 数据约定：Rule 汇集 Id、Depends、Breaks，供该模块传递状态。
    private sealed record Rule(string Id, Changes Depends, Changes Breaks);
    private readonly List<Rule> rules = [];
    /// <summary>使用实例路径边界和大小限制读取配置；解析所有规则后才允许求解。</summary>
    public static QuiltDependencyOverrides Read(string directory)
    {
        var result = new QuiltDependencyOverrides();
        var path = ResourceInstaller.SafeTarget(directory, RelativePath);
        if (!File.Exists(path))
            return result;
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Quilt 依赖覆盖配置超过 2 MiB。");
        var root = QuiltMetadataJson.Parse(
            File.ReadAllText(path)
        );
        if (root["schema_version"]?.Type != JTokenType.Integer || (int)root["schema_version"]! != 1)
            throw new NotSupportedException("仅支持 Quilt 依赖覆盖 schema_version 1。");
        if (root["overrides"] is null)
            return result;
        if (root["overrides"] is not JArray entries || entries.Count > 512)
            throw new InvalidDataException("Quilt overrides 必须是最多 512 项的数组。");
        // 官方按路径和 Java 正则匹配；本地尚未复现这两种语义，因此明确停止，避免忽略配置。
        foreach (var entry in entries)
        {
            // [实现] 对 entries 中的每一项：按 entry is not JObject obj || obj["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((… 分支处理；按 obj["version"] is not null 分支处理；准备 var id = (string)obj["id"]!。
            if (entry is not JObject obj || obj["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string? )obj["id"]) || obj["path"] is not null || obj["pattern"] is not null)
                throw new NotSupportedException("Quilt 覆盖目前支持精确 id；path / pattern 选择器尚未支持。");
            if (obj["version"] is not null)
                throw new NotSupportedException("Quilt 版本覆盖尚未完成官方行为对照，不能静态判定兼容。");
            var id = (string)obj["id"]!;
            // 官方保存为无序规则集合；同一 id 多条规则存在顺序歧义，要求合并后再导入。
            if (result.rules.Any(
                x => x.Id == id
            ))
                throw new NotSupportedException("同一 Quilt id 存在多条覆盖规则，请合并：" + id);
            result.rules.Add(
                new(
                    id,
                    ParseChanges(obj["depends"]),
                    ParseChanges(obj["breaks"])
                )
            );
        }

        return result;
    }

    /// <summary>把 add、remove、replace/with 分组；官方执行顺序为替换、移除、追加。</summary>
    private static Changes ParseChanges(JToken? value)
    {
        var replacements = new List<(JToken, JToken)>();
        var removals = new List<JToken>();
        var additions = new List<JToken>();
        if (value is null)
            return new(replacements, removals, additions);
        var entries = value is JArray a
            ? a
            : value is JObject
                ? new JArray(
                    value.DeepClone()
                )
                : throw new InvalidDataException("Quilt 依赖覆盖必须是对象或对象数组。");
        if (entries.Count > 1024)
            throw new InvalidDataException("Quilt 依赖覆盖操作过多。");
        foreach (var entry in entries)
        {
            // [实现] 对 entries 中的每一项：按 entry is not JObject obj 分支处理；准备 var keys = new[] { "add", "remove", "replace", "with" }.Where( k => obj[k] is not null ) .ToArr…；按 keys.SequenceEqual( new[] { "add" } ) 分支处理。
            if (entry is not JObject obj)
                throw new InvalidDataException("Quilt 覆盖操作必须是对象。");
            var keys = new[]
            {
                "add",
                "remove",
                "replace",
                "with"
            }.Where(
                k => obj[k] is not null
            )
                .ToArray();
            if (keys.SequenceEqual(
                new[]
                {
                    "add"
                }
            ))
                additions.Add(
                    ValidateDependency(obj["add"]!)
                );
            else if (keys.SequenceEqual(
                new[]
                {
                    "remove"
                }
            ))
                removals.Add(
                    ValidateDependency(obj["remove"]!)
                );
            else if (keys.SequenceEqual(
                new[]
                {
                    "replace",
                    "with"
                }
            ))
            {
                // [实现] 条件成立（keys.SequenceEqual( new[] { "replace", "with" } )）时：准备 var from = ValidateDependency(obj["replace"]!)；按 replacements.Any( x => Equal(x.Item1, from) ) 分支处理；执行 replacements.Add( (from, ValidateDependency(obj["with"]!)) )。
                var from = ValidateDependency(obj["replace"]!);
                // 无序 replacement map 中的链式替换可能依赖遍历顺序；在 Apply 中再检查交叉影响。
                if (replacements.Any(
                    x => Equal(x.Item1, from)
                ))
                    throw new InvalidDataException("重复的 Quilt replace 条件。");
                replacements.Add(
                    (from, ValidateDependency(obj["with"]!))
                );
            }
            else
                throw new InvalidDataException("Quilt 覆盖需要 add、remove 或 replace 与 with 配对。");
        }

        if (replacements.Any(
            x => replacements.Any(
                y => Equal(x.Item2, y.Item1)
            )
        ))
            throw new NotSupportedException("Quilt 连锁 replace 存在执行顺序歧义，请合并覆盖规则。");
        return new(replacements, removals, additions);
    }

    /// <summary>校验声明形状并展开嵌套数组，保持官方依赖集合的顺序敏感相等性。</summary>
    private static JToken ValidateDependency(JToken value, int depth = 0)
    {
        if (depth > 32)
            throw new InvalidDataException("Quilt 覆盖依赖嵌套过深。");
        if (value is JArray array)
        {
            // [实现] 条件成立（value is JArray array）时：准备 var flat = new JArray()；遍历 array 处理 child；返回 flat。
            var flat = new JArray();
            foreach (var child in array)
            {
                // [实现] 对 array 中的每一项：准备 var parsed = ValidateDependency(child, depth + 1)；按 parsed is JArray nested 分支处理。
                var parsed = ValidateDependency(child, depth + 1);
                if (parsed is JArray nested)
                    foreach (var item in nested)
                        flat.Add(
                            item.DeepClone()
                        );
                else
                    flat.Add(parsed);
            }

            return flat;
        }

        var obj = value.Type == JTokenType.String
            ? new JObject
            {
                ["id"] = (string)value!
            }

            : value is JObject original
                ? (JObject)original.DeepClone()
                : throw new InvalidDataException("Quilt 覆盖依赖需要字符串、对象或数组。");
        if (obj["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string? )obj["id"]) || ((string)obj["id"]!).Split(':')
            .Length > 2)
            throw new InvalidDataException("Quilt 覆盖依赖 id 无效。");
        if (obj["optional"] is { Type: not JTokenType.Boolean })
            throw new InvalidDataException("optional 必须是布尔值。");
        // 即使规则未匹配任何模组，也不能把损坏的版本条件当作有效覆盖文件。
        _ = QuiltVersionRange.Matches("0", obj["versions"]);
        if (obj["unless"] is { } unless)
            obj["unless"] = ValidateDependency(unless, depth + 1);
        return obj;
    }

    /// <summary>比较 id、标准化版本区间、optional 和 unless；说明用的 reason 不影响依赖身份。</summary>
    private static bool Equal(JToken left, JToken right)
    {
        left = ValidateDependency(left);
        right = ValidateDependency(right);
        if (left is JArray || right is JArray)
        {
            // [实现] 条件成立（left is JArray || right is JArray）时：按 left is not JArray l || right is not JArray r || l.Count != r.Count 分支处理；返回 l.Zip(r) .All( x => Equal(x.First, x.Second) )。
            if (left is not JArray l || right is not JArray r || l.Count != r.Count)
                return false;
            return l.Zip(r)
                .All(
                    x => Equal(x.First, x.Second)
                );
        }

        return (string? )left["id"] == (string? )right["id"] && ((bool? )left["optional"] ?? false) == ((bool? )right["optional"] ?? false) && QuiltVersionRange.Equivalent(left["versions"], right["versions"]) && (left["unless"] is null
            ? right["unless"] is null
            : right["unless"] is { } other && Equal(left["unless"]!, other));
    }

    /// <summary>复制候选并应用匹配规则；返回诊断信息，让界面展示覆盖实际影响的模组。</summary>
    public IReadOnlyList<QuiltCandidate> Apply(IEnumerable<QuiltCandidate> candidates, List<string> warnings)
    {
        var result = new List<QuiltCandidate>();
        var matched = new HashSet<string>();
        foreach (var candidate in candidates)
        {
            // [实现] 对 candidates 中的每一项：准备 var rule = rules.SingleOrDefault( x => x.Id == candidate.Id )；按 rule is null 分支处理；按 candidate.Fabric 分支处理。
            var rule = rules.SingleOrDefault(
                x => x.Id == candidate.Id
            );
            if (rule is null)
            {
                // [实现] 条件成立（rule is null）时：执行 result.Add(candidate)；执行 continue;。
                result.Add(candidate);
                continue;
            }

            // Fabric 到 Quilt 的转换会改变范围语义；未对照该适配层前不替换 Fabric 依赖。
            if (candidate.Fabric)
                throw new NotSupportedException("对 Fabric 模组应用 Quilt 覆盖尚未完成对照：" + candidate.Id);
            matched.Add(rule.Id);
            result.Add(
                candidate with
                {
                    Depends = ApplyChanges(candidate.Depends, rule.Depends, candidate.Id, warnings),
                    Breaks = ApplyChanges(candidate.Breaks, rule.Breaks, candidate.Id, warnings)
                }
            );
        }

        foreach (var rule in rules)
            warnings.Add(
                matched.Contains(rule.Id)
                    ? "已应用 Quilt 依赖覆盖：" + rule.Id + "；覆盖不保证运行时兼容。"
                    : "Quilt 依赖覆盖未匹配当前候选：" + rule.Id
            );
        return result;
    }

    /// <summary>按官方顺序变换依赖集合；找不到旧条件时保留原约束并报告，不擅自放宽版本。</summary>
    private static JArray ApplyChanges(JArray original, Changes changes, string id, List<string> warnings)
    {
        var result = new JArray();
        foreach (var token in original)
            AddUnique(token);
        foreach (var replacement in changes.Replacements)
            if (Remove(replacement.From))
                AddUnique(replacement.To);
        foreach (var removal in changes.Removals)
            Remove(removal);
        foreach (var addition in changes.Additions)
            AddUnique(addition);
        return result;
        // JToken 必须深拷贝，避免修改缓存中的元数据或其他回溯分支。
        void AddUnique(JToken token)
        {
            if (!result.Any(
                x => Equal(x, token)
            ))
                result.Add(
                    token.DeepClone()
                );
        }

        // [实现] Remove 的实现：准备 var found = result.FirstOrDefault( x => Equal(x, token) )；按 found is not null 分支处理；执行 warnings.Add( "Quilt 覆盖未找到待替换/移除依赖：" + id + " / " + token.ToString(Newtonsoft.Json.Formatting.N…。
        bool Remove(JToken token)
        {
            var found = result.FirstOrDefault(
                x => Equal(x, token)
            );
            if (found is not null)
            {
                // [实现] 条件成立（found is not null）时：执行 found.Remove()；返回 true。
                found.Remove();
                return true;
            }

            warnings.Add(
                "Quilt 覆盖未找到待替换/移除依赖：" + id + " / " + token.ToString(Newtonsoft.Json.Formatting.None)
            );
            return false;
        }
    }
}

/// <summary>正式 Quilt 元数据及 overrides 使用严格 JSON；JSON5 不能用于普通游戏环境。</summary>
internal static class QuiltMetadataJson
{
    // [实现] Parse 的实现：执行受异常处理保护的操作。
    public static JObject Parse(string text)
    {
        try
        {
            // 先用严格解析器拒绝注释、单引号、尾逗号和额外文档，再转入现有 JToken 数据模型。
            using var document = System.Text.Json.JsonDocument.Parse(
                text,
                new()
                {
                    MaxDepth = 64
                }
            );
            return JObject.Parse(
                document.RootElement.GetRawText(),
                new()
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                }
            );
        }
        catch (Exception ex)when (ex is System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        {
            // [实现] 捕获 (Exception ex) 后：抛出 new InvalidDataException("Quilt 元数据需要有效的严格 JSON：" + ex.Message, ex)。
            throw new InvalidDataException("Quilt 元数据需要有效的严格 JSON：" + ex.Message, ex);
        }
    }

    // [实现] CheckProductionArchive 的实现：按 zip.GetEntry("quilt.mod.json5")is not null 分支处理。
    public static void CheckProductionArchive(System.IO.Compression.ZipArchive zip)
    {
        if (zip.GetEntry("quilt.mod.json5")is not null)
            throw new InvalidDataException("quilt.mod.json5 仅用于 Quilt 开发环境；请获取作者构建的正式 JAR（quilt.mod.json），不能直接安装开发包。");
    }
}
