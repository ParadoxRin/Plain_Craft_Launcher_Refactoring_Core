using Newtonsoft.Json.Linq;
using Pcl.Core.Adapters.Compatibility;

namespace Pcl.Core;
/// <summary>
/// 读取实例自带的 Quilt schema 1 依赖覆盖配置，支持路径、ID和Java正则选择器。
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
    private sealed record Rule(
        int Number,
        string Kind,
        string Selector,
        string? Version,
        Changes Depends,
        Changes Breaks
    );
    private readonly List<Rule> rules = [];
    private string directory = "";
    private string contextDirectory = "";
    private int javaMajor;
    private QuiltOverrideRuntime? runtime;
    private CancellationToken cancellation;
    /// <summary>Parse every declaration, including unmatched rules; duplicate path keys retain the last complete rule like Quilt's map.</summary>
    public static QuiltDependencyOverrides Read(
        string directory,
        string? contextDirectory = null,
        int javaMajor = 21,
        QuiltOverrideRuntime? runtime = null,
        CancellationToken ct = default
    )
    {
        var result = new QuiltDependencyOverrides
        {
            directory = directory,
            contextDirectory = contextDirectory ?? directory,
            javaMajor = javaMajor,
            runtime = runtime,
            cancellation = ct
        };
        var path = ResourceInstaller.SafeTarget(directory, RelativePath);
        if (!File.Exists(path))
            return result;
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Quilt 依赖覆盖配置超过2 MiB。");
        var root = QuiltMetadataJson.Parse(
            File.ReadAllText(path)
        );
        if (root["schema_version"]?.Type != JTokenType.Integer || (int)root["schema_version"]! != 1)
            throw new NotSupportedException("仅支持 Quilt 依赖覆盖 schema_version 1。");
        if (root["overrides"] is null)
            return result;
        if (root["overrides"] is not JArray entries || entries.Count > 512)
            throw new InvalidDataException("Quilt overrides 必须是最多512项的数组。");
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (entries[i] is not JObject obj)
                throw new InvalidDataException("Quilt 覆盖规则必须是对象。");
            var selectors = new[]
            {
                "path",
                "id",
                "pattern"
            }.Where(
                key => obj.Property(key)is not null
            )
                .ToArray();
            if (selectors.Length != 1 || obj[selectors[0]]?.Type != JTokenType.String)
                throw new InvalidDataException("Quilt 覆盖需要且只能有一个字符串 path、id 或 pattern。");
            var kind = selectors[0];
            var selector = (string)obj[kind]!;
            if (selector.Length > 4096)
                throw new NotSupportedException("Quilt 选择器超过4096字符预算。");
            if (obj["version"] is { Type: not JTokenType.String })
                throw new InvalidDataException("Quilt version 覆盖必须是字符串。");
            var rule = new Rule(
                i,
                kind,
                selector,
                (string? )obj["version"],
                ParseChanges(obj["depends"]),
                ParseChanges(obj["breaks"])
            );
            if (kind == "path")
                result.rules.RemoveAll(
                    previous => previous.Kind == "path" && previous.Selector == selector
                );
            result.rules.Add(rule);
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
                replacements.RemoveAll(
                    previous => Equal(previous.Item1, from)
                );
                replacements.Add(
                    (from, ValidateDependency(obj["with"]!))
                );
            }
            else
                throw new InvalidDataException("Quilt 覆盖需要 add、remove 或 replace 与 with 配对。");
        }

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

    /// <summary>Path changes precede ID/pattern changes. Unordered overlaps must have one result for every bounded ordering.</summary>
    public IReadOnlyList<QuiltCandidate> Apply(IEnumerable<QuiltCandidate> candidates, List<string> warnings)
    {
        var source = candidates.ToArray();
        var matched = new HashSet<int>();
        var patterns = rules.Where(
            rule => rule.Kind == "pattern"
        )
            .ToArray();
        QuiltOverrideRuntime Runtime() => runtime ??= QuiltOverrideRuntime.ForInstance(contextDirectory, javaMajor, source, cancellation);
        var patternResults = patterns.Length == 0
            ? []
            : Runtime()
                .Match(
                    patterns.Select(
                        rule => rule.Selector
                    )
                        .ToArray(),
                    source.Select(
                        candidate => candidate.Id
                    )
                        .ToArray(),
                    cancellation
                );
        var result = new List<QuiltCandidate>();
        for (var i = 0; i < source.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var candidate = source[i];
            var path = DescribePath(candidate.Source, directory);
            var pathRule = rules.SingleOrDefault(
                rule => rule.Kind == "path" && rule.Selector == path
            );
            var selected = rules.Where(
                rule => rule.Kind == "id" && rule.Selector == candidate.Id || rule.Kind == "pattern" && patternResults[Array.IndexOf(patterns, rule)][i]
            )
                .ToArray();
            var all = (pathRule is null
                ? selected
                : selected.Prepend(pathRule)).ToArray();
            foreach (var rule in all)
                matched.Add(rule.Number);
            if (candidate.Fabric && !candidate.QuiltDependencySemantics && all.Any(
                rule => Changed(rule.Depends) || Changed(rule.Breaks)
            ))
                candidate = Runtime()
                    .ConvertFabric(candidate, cancellation);
            if (pathRule is not null)
                candidate = ApplyRule(candidate, pathRule, true, warnings);
            if (selected.All(
                rule => OnlyAdds(rule.Depends) && OnlyAdds(rule.Breaks)
            ))
            {
                foreach (var rule in selected)
                    candidate = ApplyRule(candidate, rule, false, warnings);
            }
            else
            {
                candidate = StableOrder(
                    candidate,
                    selected.Select<Rule, Func<QuiltCandidate, QuiltCandidate>>(
                        rule => value => ApplyRule(value, rule, false, warnings)
                    )
                        .ToArray(),
                    (left, right) => SameSet(left.Depends, right.Depends) && SameSet(left.Breaks, right.Breaks),
                    value => Key(value.Depends) + "|" + Key(value.Breaks)
                );
            }

            result.Add(candidate);
        }

        foreach (var rule in rules)
        {
            Warn(
                warnings,
                matched.Contains(rule.Number)
                    ? "已应用 Quilt 依赖覆盖：" + rule.Kind + " " + rule.Selector + "；覆盖不保证运行时兼容。"
                    : "Quilt 依赖覆盖未匹配当前候选：" + rule.Selector
            );
            if (rule.Version is not null)
                Warn(warnings, "Quilt 0.27.1/0.30.1 仅解析 version 覆盖字段而不应用；未更改运行时版本：" + rule.Selector);
        }

        return result;
    }

    private static bool Changed(Changes value) => value.Replacements.Count + value.Removals.Count + value.Additions.Count != 0;
    private static bool OnlyAdds(Changes value) => value.Replacements.Count + value.Removals.Count == 0;
    private static bool SameSet(JArray left, JArray right) => left.Count == right.Count && left.All(
        value => right.Any(
            other => Equal(value, other)
        )
    );
    private static string Key(JArray values) => string.Join(
        "|",
        values.Select(
            value => value.ToString(Newtonsoft.Json.Formatting.None)
        )
            .Order(StringComparer.Ordinal)
    );
    private static void Warn(List<string> warnings, string message)
    {
        if (warnings.Count < 128 && !warnings.Contains(message))
            warnings.Add(message);
    }

    /// <summary>Describe installed or staged files exactly as ordinary Quilt mod origins; retain case and nested archive boundaries.</summary>
    public static string DescribePath(string source, string directory)
    {
        if (source.StartsWith("builtin:", StringComparison.Ordinal))
            return source;
        var boundary = source.IndexOf('!');
        var root = boundary < 0
            ? source
            : source[..boundary];
        if (!Path.IsPathFullyQualified(root))
            return source;
        var mods = Path.Combine(
            Path.GetFullPath(directory),
            "mods"
        ) + Path.DirectorySeparatorChar;
        var game = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(root);
        var description = absolute.StartsWith(mods, StringComparison.Ordinal)
            ? "<mods>/" + absolute[mods.Length..]
            : absolute.StartsWith(game, StringComparison.Ordinal)
                ? "<game>/" + absolute[game.Length..]
                : absolute;
        // An archive root contributes its slash before the parent archive boundary.
        // Quilt describes nested origins as outer.jar!/lib/child.jar, retaining every boundary.
        if (boundary >= 0)
            description += source[boundary..];
        return description.Replace(Path.DirectorySeparatorChar, '/');
    }

    private QuiltCandidate ApplyRule(QuiltCandidate candidate, Rule rule, bool fuzzy, List<string> warnings) => candidate with
    {
        Depends = ApplyChanges(candidate.Depends, rule.Depends, candidate.Id, fuzzy, warnings),
        Breaks = ApplyChanges(candidate.Breaks, rule.Breaks, candidate.Id, fuzzy, warnings)
    };
    /// <summary>Explore unordered rule maps with deduplication; never invent an ordering when it changes compatibility.</summary>
    private T StableOrder<T>(
        T initial,
        IReadOnlyList<Func<T, T>> operations,
        Func<T, T, bool> equal,
        Func<T, string> key,
        Func<T, T>? finish = null
    )
    {
        if (operations.Count == 0)
            return initial;
        if (operations.Count > 12)
            throw new NotSupportedException("Quilt 重叠覆盖超过12条无序操作预算，请合并规则。");
        var pending = new Stack<(T Value, int Mask)>();
        pending.Push((initial, 0));
        var seen = new HashSet<string>();
        var complete = (1 << operations.Count) - 1;
        T? final = default;
        var found = false;
        while (pending.TryPop(out var state))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(
                state.Mask + ":" + key(state.Value)
            ))
                continue;
            if (seen.Count > 4096)
                throw new NotSupportedException("Quilt 无序覆盖检查超过4096状态，尚未判定兼容性。");
            if (state.Mask == complete)
            {
                var completed = finish is null
                    ? state.Value
                    : finish(state.Value);
                if (found && !equal(final!, completed))
                    throw new NotSupportedException("Quilt 重叠/连锁覆盖随无序执行顺序改变结果，请合并为无歧义规则。");
                final = completed;
                found = true;
                continue;
            }

            for (var i = 0; i < operations.Count; i++)
            {
                if ((state.Mask & (1 << i)) == 0)
                    pending.Push(
                        (operations[i](state.Value), state.Mask | (1 << i))
                    );
            }
        }

        return final!;
    }

    /// <summary>Replacement maps are unordered, followed by removals and additions. Only path selectors enable unique-ID fuzzy removal.</summary>
    private JArray ApplyChanges(JArray original, Changes changes, string id, bool fuzzy, List<string> warnings)
    {
        JArray Copy(JArray source)
        {
            var result = new JArray();
            foreach (var token in source)
                Add(result, token);
            return result;
        }

        void Add(JArray result, JToken token)
        {
            if (!result.Any(
                value => Equal(value, token)
            ))
                result.Add(
                    ValidateDependency(token)
                );
        }

        bool Remove(JArray result, JToken token)
        {
            var found = result.FirstOrDefault(
                value => Equal(value, token)
            );
            if (found is null && fuzzy && token is not JArray && token["unless"] is null && QuiltVersionRange.Equivalent(token["versions"], null))
            {
                var matches = result.Where(
                    value => value is not JArray && (string? )value["id"] == (string? )token["id"]
                )
                    .ToArray();
                if (matches.Length == 1)
                    found = matches[0];
            }

            if (found is not null)
            {
                found.Remove();
                return true;
            }

            Warn(
                warnings,
                "Quilt 覆盖未找到唯一待替换/移除依赖：" + id + " / " + token.ToString(Newtonsoft.Json.Formatting.None)
            );
            return false;
        }

        var value = Copy(original);
        var operations = changes.Replacements.Select<(JToken From, JToken To), Func<JArray, JArray>>(
            replacement => source =>
            {
                var result = Copy(source);
                if (Remove(result, replacement.From))
                    Add(result, replacement.To);
                return result;
            }
        )
            .ToArray();
        var chained = changes.Replacements.Any(
            left => changes.Replacements.Any(
                right => Equal(left.To, right.From)
            )
        );
        JArray Additions(JArray source)
        {
            var result = Copy(source);
            foreach (var addition in changes.Additions)
                Add(result, addition);
            return result;
        }

        JArray Remainder(JArray source)
        {
            var removals = changes.Removals.Select<JToken, Func<JArray, JArray>>(
                token => input =>
                {
                    var result = Copy(input);
                    Remove(result, token);
                    return result;
                }
            )
                .ToArray();
            // Quilt stores removals in an ordered List, retaining repeated fuzzy removals.
            foreach (var removal in removals)
                source = removal(source);
            return Additions(source);
        }

        if (chained || fuzzy && operations.Length > 1)
            return StableOrder(value, operations, SameSet, Key, Remainder);
        foreach (var operation in operations)
            value = operation(value);
        value = Remainder(value);
        return value;
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
