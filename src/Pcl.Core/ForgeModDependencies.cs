using System.Collections;
using System.IO.Compression;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Tomlyn;
using Tomlyn.Model;

namespace Pcl.Core;
// [实现] 数据约定：ForgeDependency 汇集 Id、Range、Type、Side、Ordering，供该模块传递状态。
public sealed record ForgeDependency(string Id, string Range, string Type, string Side, string Ordering);
// [实现] 数据约定：ForgeModMetadata 汇集 Id、Name、Version、Dependencies、LoaderRange、JavaRange、Nested、OpenGlRange、LanguageLoader，供该模块传递状态。
public sealed record ForgeModMetadata(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<ForgeDependency> Dependencies,
    string LoaderRange,
    string? JavaRange,
    bool Nested,
    string? OpenGlRange = null,
    string LanguageLoader = "javafml"
);
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class MavenVersionRange
{
    // [实现] Matches 的实现：按 range is "" or "*" || string.IsNullOrWhiteSpace(range) 分支处理；执行 range = range.Trim()；按 range.Length > 4096 分支处理。
    public static bool Matches(string version, string range)
    {
        if (range is "" or "*" || string.IsNullOrWhiteSpace(range))
            return true;
        range = range.Trim();
        if (range.Length > 4096)
            throw new InvalidDataException("版本范围过长。");
        // Maven bare versions are recommendations, not hard restrictions.
        if (range[0] is not ('[' or '('))
        {
            // [实现] 条件成立（range[0] is not ('[' or '(')）时：执行 _ = Compare(range, range)；返回 true。
            _ = Compare(range, range);
            return true;
        }

        var matches = Regex.Matches(range, @"([\[(])([^\]\)]*)([\])])");
        if (matches.Count == 0)
            throw new InvalidDataException("无法解析版本范围：" + range);
        var cursor = 0;
        var accepted = false;
        string? previousUpper = null;
        foreach (Match match in matches)
        {
            // [实现] 对 matches 中的每一项：准备 var separator = range[cursor..match.Index].Trim()；按 separator.Length > 0 && (cursor == 0 || separator != ",") 分支处理；执行 cursor = match.Index + match.Length。
            var separator = range[cursor..match.Index].Trim();
            if (separator.Length > 0 && (cursor == 0 || separator != ","))
                throw new InvalidDataException("版本范围格式错误：" + range);
            cursor = match.Index + match.Length;
            var parts = match.Groups[2].Value.Split(',');
            if (parts.Length == 1)
            {
                // [实现] 条件成立（parts.Length == 1）时：按 match.Groups[1].Value != "[" || match.Groups[3].Value != "]" 分支处理；准备 var exact = parts[0].Trim()；按 exact.Length == 0 分支处理。
                if (match.Groups[1].Value != "[" || match.Groups[3].Value != "]")
                    throw new InvalidDataException("精确版本范围需要方括号。");
                var exact = parts[0].Trim();
                if (exact.Length == 0)
                    throw new InvalidDataException("精确版本不能为空。");
                if (previousUpper is not null && Compare(exact, previousUpper) < 0)
                    throw new InvalidDataException("版本区间重叠或未排序。");
                previousUpper = exact;
                accepted |= Compare(version, exact) == 0;
            }
            else if (parts.Length == 2)
            {
                // [实现] 条件成立（parts.Length == 2）时：准备 var lower = parts[0].Trim()；准备 var upper = parts[1].Trim()；按 lower.Length > 0 && upper.Length > 0 && (Compare(lower, upper) > 0 || (Compare(lower, upper) ==… 分支处理。
                var lower = parts[0].Trim();
                var upper = parts[1].Trim();
                if (lower.Length > 0 && upper.Length > 0 && (Compare(lower, upper) > 0 || (Compare(lower, upper) == 0 && (match.Groups[1].Value != "[" || match.Groups[3].Value != "]"))))
                    throw new InvalidDataException("版本区间上下界无效。");
                if (previousUpper is not null && (lower.Length == 0 || Compare(lower, previousUpper) < 0))
                    throw new InvalidDataException("版本区间重叠或未排序。");
                previousUpper = upper.Length == 0
                    ? null
                    : upper;
                var low = lower.Length == 0 || (match.Groups[1].Value == "["
                    ? Compare(version, lower) >= 0
                    : Compare(version, lower) > 0);
                var high = upper.Length == 0 || (match.Groups[3].Value == "]"
                    ? Compare(version, upper) <= 0
                    : Compare(version, upper) < 0);
                accepted |= low && high;
            }
            else
                throw new InvalidDataException("版本范围格式错误：" + range);
        }

        if (cursor != range.Length)
            throw new InvalidDataException("版本范围格式错误：" + range);
        return accepted;
    }

    // [实现] Compare 的实现：计算并返回 MavenComparableVersion.Compare(left, right)。
    public static int Compare(string left, string right) => MavenComparableVersion.Compare(left, right);
}

// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class ForgeModReader
{
    // [实现] Read 的实现：准备 var input = File.OpenRead(file)；返回 Read(input, loader, gameVersion, false, 0)。
    public static IReadOnlyList<ForgeModMetadata> Read(string file, string loader, string gameVersion)
    {
        using var input = File.OpenRead(file);
        return Read(input, loader, gameVersion, false, 0);
    }

    // [实现] Text 的实现：按 entry.Length > 2 * 1024 * 1024 分支处理；准备 var reader = new StreamReader( entry.Open() )；返回 reader.ReadToEnd()。
    private static string Text(ZipArchiveEntry entry)
    {
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("模组元数据过大。");
        using var reader = new StreamReader(
            entry.Open()
        );
        return reader.ReadToEnd();
    }

    // [实现] Tables 的实现：计算并返回 value is IEnumerable items ? items.Cast<object>() .OfType<TomlTable>() : []。
    private static IEnumerable<TomlTable> Tables(object? value) => value is IEnumerable items
        ? items.Cast<object>()
            .OfType<TomlTable>()
        : [];
    // [实现] Obj 的实现：计算并返回 table is not null && table.TryGetValue(key, out var value) ? value : null。
    private static object? Obj(TomlTable? table, string key) => table is not null && table.TryGetValue(key, out var value)
        ? value
        : null;
    // [实现] Value 的实现：计算并返回 table.TryGetValue(key, out var value) ? value?.ToString() ?? fallback : fallback。
    private static string Value(TomlTable table, string key, string fallback = "") => table.TryGetValue(key, out var value)
        ? value?.ToString() ?? fallback
        : fallback;
    // [实现] ReadSingle 的实现：计算并返回 Read(input, loader, gameVersion, nested, 0, false)。
    internal static IReadOnlyList<ForgeModMetadata> ReadSingle(Stream input, string loader, string gameVersion, bool nested) => Read(input, loader, gameVersion, nested, 0, false);
    // [实现] Read 的实现：按 depth > 8 分支处理；准备 var zip = new ZipArchive(input, ZipArchiveMode.Read, true)；准备 var entry = zip.GetEntry( loader == "neoforge" && gameVersion is not ("1.20.1" or "1.20.2" or "…。
    private static IReadOnlyList<ForgeModMetadata> Read(
        Stream input,
        string loader,
        string gameVersion,
        bool nested,
        int depth,
        bool recurse = true
    )
    {
        if (depth > 8)
            throw new InvalidDataException("嵌套模组过深。");
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        var entry = zip.GetEntry(
            loader == "neoforge" && gameVersion is not ("1.20.1" or "1.20.2" or "1.20.3" or "1.20.4")
                ? "META-INF/neoforge.mods.toml"
                : "META-INF/mods.toml"
        );
        var result = new List<ForgeModMetadata>();
        var libraryOnly = zip.GetEntry("META-INF/MANIFEST.MF")is { } typeManifest && Regex.IsMatch(
            Text(typeManifest),
            @"(?m)^FMLModType:\s*(LIBRARY|LANGPROVIDER|GAMELIBRARY)\s*$"
        );
        if (libraryOnly)
        {
            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
        }
        else if (entry is not null)
        {
            // [实现] 条件成立（entry is not null）时：准备 TomlTable table；执行受异常处理保护的操作；准备 var manifest = zip.GetEntry("META-INF/MANIFEST.MF")is { } mf ? Text(mf) : ""。
            TomlTable table;
            try
            {
                // [实现] 在异常保护范围内：执行 table = TomlSerializer.Deserialize<TomlTable>( Text(entry) ) ?? throw new InvalidDataException(…。
                table = TomlSerializer.Deserialize<TomlTable>(
                    Text(entry)
                ) ?? throw new InvalidDataException("空 TOML 元数据。");
            }
            catch (Exception ex)when (ex is not InvalidDataException and not OutOfMemoryException)
            {
                // [实现] 捕获 (Exception ex) 后：抛出 new InvalidDataException("模组 TOML 无法解析：" + ex.Message, ex)。
                throw new InvalidDataException("模组 TOML 无法解析：" + ex.Message, ex);
            }

            var manifest = zip.GetEntry("META-INF/MANIFEST.MF")is { } mf
                ? Text(mf)
                : "";
            var jarVersion = Regex.Match(
                manifest.Replace("\r\n ", ""),
                @"(?m)^Implementation-Version:\s*([^\r\n]+)"
            )
                .Groups[1].Value;
            var properties = Obj(table, "properties") as TomlTable;
            // [实现] Expand 的实现：在 round < 8 && value.Contains("${file.", StringComparison.Ordinal) 条件下迭代；按 value.Contains("${", StringComparison.Ordinal) 分支处理；返回 value。
            string Expand(string value)
            {
                for (int round = 0; round < 8 && value.Contains("${file.", StringComparison.Ordinal); round++)
                {
                    // [实现] 每次迭代（round < 8 && value.Contains("${file.", StringComparison.Ordinal)）：准备 var next = Regex.Replace( value, @"\$\{file\.([^}]+)\}", m => m.Groups[1].Value == "jarVersion"…；按 next == value 分支处理；执行 value = next。
                    var next = Regex.Replace(
                        value,
                        @"\$\{file\.([^}]+)\}",
                        m => m.Groups[1].Value == "jarVersion"
                            ? (jarVersion.Length > 0
                                ? jarVersion
                                : "0.0NONE")
                            : Obj(properties, m.Groups[1].Value)?.ToString() ?? m.Value
                    );
                    if (next == value)
                        break;
                    value = next;
                }

                if (value.Contains("${", StringComparison.Ordinal))
                    throw new InvalidDataException("无法解析模组属性：" + value);
                return value;
            }

            var dependencies = Obj(table, "dependencies") as TomlTable;
            var features = Obj(table, "features") as TomlTable;
            foreach (var mod in Tables(
                Obj(table, "mods")
            ))
            {
                // [实现] 对 Tables( Obj(table, "mods") ) 中的每一项：准备 var id = Value(mod, "modId")；按 id.Length == 0 分支处理；准备 var version = Expand( Value(mod, "version", "1") )。
                var id = Value(mod, "modId");
                if (id.Length == 0)
                    throw new InvalidDataException("模组缺少 modId。");
                var version = Expand(
                    Value(mod, "version", "1")
                );
                if (version == "${file.jarVersion}")
                    version = jarVersion;
                if (string.IsNullOrWhiteSpace(version) || version.Contains("${", StringComparison.Ordinal))
                    throw new InvalidDataException("无法确定 " + id + " 的版本。");
                var requirements = new List<ForgeDependency>();
                foreach (var dep in Tables(
                    Obj(dependencies, id)
                ))
                {
                    // [实现] 对 Tables( Obj(dependencies, id) ) 中的每一项：准备 var type = Value( dep, "type", Obj(dep, "mandatory")is false ? "optional" : "required" )；按 type is not ("required" or "optional" or "incompatible" or "discouraged") 分支处理；准备 var side = Value(dep, "side", "BOTH")。
                    var type = Value(
                        dep,
                        "type",
                        Obj(dep, "mandatory")is false
                            ? "optional"
                            : "required"
                    );
                    if (type is not ("required" or "optional" or "incompatible" or "discouraged"))
                        throw new NotSupportedException("未知 NeoForge 依赖类型：" + type);
                    var side = Value(dep, "side", "BOTH");
                    var ordering = Value(dep, "ordering", "NONE");
                    if (side is not ("BOTH" or "CLIENT" or "SERVER") || ordering is not ("NONE" or "BEFORE" or "AFTER"))
                        throw new InvalidDataException("无效的依赖端或排序声明。");
                    requirements.Add(
                        new(
                            Value(dep, "modId"),
                            Expand(
                                Value(dep, "versionRange")
                            ),
                            type,
                            side,
                            ordering
                        )
                    );
                }

                var feature = Obj(features, id) as TomlTable;
                result.Add(
                    new(
                        id,
                        Value(mod, "displayName", id),
                        version,
                        requirements,
                        Expand(
                            Value(table, "loaderVersion")
                        ),
                        feature is not null
                            ? Value(feature, "javaVersion", "")
                            : null,
                        nested,
                        feature is not null
                            ? Value(feature, "openGLVersion", "")
                            : null,
                        Value(table, "modLoader", "javafml")
                    )
                );
            }
        }
        else if (!nested && zip.GetEntry("mcmod.info")is { } legacy)
        {
            // [实现] 条件成立（!nested && zip.GetEntry("mcmod.info")is { } legacy）时：准备 var json = JToken.Parse( Text(legacy) )；准备 var list = json as JArray ?? json["modList"] as JArray ?? new JArray(json)；遍历 list 处理 mod。
            var json = JToken.Parse(
                Text(legacy)
            );
            var list = json as JArray ?? json["modList"] as JArray ?? new JArray(json);
            foreach (var mod in list)
            {
                // [实现] 对 list 中的每一项：准备 var id = (string? )mod["modid"]；按 id is not null 分支处理。
                var id = (string? )mod["modid"];
                if (id is not null)
                    result.Add(
                        new(
                            id,
                            (string? )mod["name"] ?? id,
                            (string? )mod["version"] ?? "0",
                            [],
                            "",
                            null,
                            nested
                        )
                    );
            }
        }
        else if (!nested && !(zip.GetEntry("META-INF/MANIFEST.MF")is { } libraryManifest && Regex.IsMatch(
            Text(libraryManifest),
            @"(?m)^FMLModType:\s*(LIBRARY|LANGPROVIDER|GAMELIBRARY)\s*$"
        )))
            throw new InvalidDataException("模组缺少 Forge/NeoForge 元数据。");
        if (recurse && zip.GetEntry("META-INF/jarjar/metadata.json")is { } jarjar)
        {
            // [实现] 条件成立（recurse && zip.GetEntry("META-INF/jarjar/metadata.json")is { } jarjar）时：准备 var json = JObject.Parse( Text(jarjar) )；遍历 json["jars"] ?? new JArray() 处理 jar。
            var json = JObject.Parse(
                Text(jarjar)
            );
            foreach (var jar in json["jars"] ?? new JArray())
            {
                // [实现] 对 json["jars"] ?? new JArray() 中的每一项：准备 var path = (string? )jar["path"] ?? throw new InvalidDataException("嵌套 JAR 缺少路径。")；执行 DependencyArchive.ValidatePath(path)；准备 var child = zip.GetEntry(path) ?? throw new InvalidDataException("缺少嵌套 JAR：" + path)。
                var path = (string? )jar["path"] ?? throw new InvalidDataException("嵌套 JAR 缺少路径。");
                DependencyArchive.ValidatePath(path);
                var child = zip.GetEntry(path) ?? throw new InvalidDataException("缺少嵌套 JAR：" + path);
                if (child.Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("嵌套 JAR 过大。");
                using var bytes = new MemoryStream();
                using (var contents = child.Open())
                    contents.CopyTo(bytes);
                bytes.Position = 0;
                result.AddRange(
                    Read(bytes, loader, gameVersion, true, depth + 1)
                );
            }
        }

        return result;
    }

    // [实现] Validate 的实现：准备 var resolution = JarJarResolver.Resolve(files, loader, gameVersion)；准备 var mods = resolution.Mods.ToArray()；准备 var available = new Dictionary<string, string> { ["minecraft"] = gameVersion, [loader] = loader…。
    public static IReadOnlyList<string> Validate(
        IEnumerable<string> files,
        string loader,
        string gameVersion,
        string loaderVersion,
        int javaMajor,
        string? fmlVersion = null
    )
    {
        var resolution = JarJarResolver.Resolve(files, loader, gameVersion, javaMajor);
        var mods = resolution.Mods.ToArray();
        var available = new Dictionary<string, string>
        {
            ["minecraft"] = gameVersion,
            [loader] = loaderVersion,
            ["java"] = javaMajor.ToString()
        };
        if (loader == "neoforge" && gameVersion == "1.20.1")
            available["forge"] = loaderVersion;
        var top = new HashSet<string>();
        var warnings = resolution.Warnings.ToList();
        foreach (var mod in mods)
        {
            // [实现] 对 mods 中的每一项：按 !top.Add(mod.Id) 分支处理；按 !available.TryGetValue(mod.Id, out var current) || MavenVersionRange.Compare(mod.Version, curre… 分支处理。
            if (!top.Add(mod.Id))
                throw new InvalidDataException("重复模组 ID：" + mod.Id);
            if (!available.TryGetValue(mod.Id, out var current) || MavenVersionRange.Compare(mod.Version, current) > 0)
                available[mod.Id] = mod.Version;
        }

        foreach (var mod in mods)
        {
            // [实现] 对 mods 中的每一项：按 mod.LanguageLoader is not ("javafml" or "lowcodefml") 分支处理；按 mod.LanguageLoader is "javafml" or "lowcodefml" && !string.IsNullOrWhiteSpace(mod.LoaderRange) … 分支处理；按 !string.IsNullOrWhiteSpace(mod.JavaRange) && !MavenVersionRange.Matches( javaMajor.ToString(), … 分支处理。
            if (mod.LanguageLoader is not ("javafml" or "lowcodefml"))
                warnings.Add($"{mod.Name} 使用 {mod.LanguageLoader} {mod.LoaderRange}；第三方语言加载器版本由游戏加载器检查。");
            if (mod.LanguageLoader is "javafml" or "lowcodefml" && !string.IsNullOrWhiteSpace(mod.LoaderRange) && !string.IsNullOrWhiteSpace(fmlVersion) && !MavenVersionRange.Matches(fmlVersion, mod.LoaderRange))
                throw new InvalidDataException($"{mod.Name} 需要 FML {mod.LoaderRange}，当前为 {fmlVersion}。");
            if (!string.IsNullOrWhiteSpace(mod.JavaRange) && !MavenVersionRange.Matches(
                javaMajor.ToString(),
                mod.JavaRange
            ))
                throw new InvalidDataException($"{mod.Name} 需要 Java {mod.JavaRange}。");
            if (!string.IsNullOrWhiteSpace(mod.OpenGlRange))
                warnings.Add($"{mod.Name} 声明 OpenGL {mod.OpenGlRange}；请以实际游戏渲染器检查为准。");
            foreach (var dependency in mod.Dependencies)
            {
                // [实现] 对 mod.Dependencies 中的每一项：按 dependency.Side.Equals("SERVER", StringComparison.OrdinalIgnoreCase) 分支处理；准备 var present = available.TryGetValue(dependency.Id, out var version)；准备 var matches = present && MavenVersionRange.Matches(version!, dependency.Range)。
                if (dependency.Side.Equals("SERVER", StringComparison.OrdinalIgnoreCase))
                    continue;
                var present = available.TryGetValue(dependency.Id, out var version);
                var matches = present && MavenVersionRange.Matches(version!, dependency.Range);
                if (dependency.Type == "required" && !matches)
                    throw new InvalidDataException(
                        $"{mod.Name} 需要 {dependency.Id} {dependency.Range}；当前 {(present
                                ? version
                                : "未安装")}。"
                    );
                if (dependency.Type == "optional" && present && !matches)
                    throw new InvalidDataException($"{mod.Name} 的可选依赖 {dependency.Id} 版本不匹配：{dependency.Range}。");
                if (dependency.Type == "incompatible" && matches)
                    throw new InvalidDataException($"{mod.Name} 与 {dependency.Id} {dependency.Range} 不兼容。");
                if (dependency.Type == "discouraged" && matches)
                    warnings.Add($"{mod.Name} 不建议与 {dependency.Id} {dependency.Range} 同时使用。");
            }
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var mod in mods)
            foreach (var dependency in mod.Dependencies.Where(
                x => !x.Side.Equals("SERVER", StringComparison.OrdinalIgnoreCase) && available.ContainsKey(x.Id) && x.Ordering != "NONE"
            ))
            {
                // [实现] 对 mod.Dependencies.Where( x => !x.Side.Equals("SERVER", StringComparison.OrdinalIgnoreCase) && av… 中的每一项：按 dependency.Ordering is not ("BEFORE" or "AFTER") 分支处理；准备 var before = dependency.Ordering == "BEFORE" ? mod.Id : dependency.Id；准备 var after = dependency.Ordering == "BEFORE" ? dependency.Id : mod.Id。
                if (dependency.Ordering is not ("BEFORE" or "AFTER"))
                    throw new InvalidDataException("未知依赖排序：" + dependency.Ordering);
                var before = dependency.Ordering == "BEFORE"
                    ? mod.Id
                    : dependency.Id;
                var after = dependency.Ordering == "BEFORE"
                    ? dependency.Id
                    : mod.Id;
                if (!edges.TryGetValue(before, out var next))
                    edges[before] = next = [];
                next.Add(after);
            }

        var complete = new HashSet<string>();
        var active = new HashSet<string>();
        // [实现] Visit 的实现：按 complete.Contains(id) 分支处理；按 !active.Add(id) 分支处理；按 edges.TryGetValue(id, out var next) 分支处理。
        void Visit(string id)
        {
            if (complete.Contains(id))
                return;
            if (!active.Add(id))
                throw new InvalidDataException("模组加载顺序循环依赖：" + id);
            if (edges.TryGetValue(id, out var next))
                foreach (var child in next)
                    Visit(child);
            active.Remove(id);
            complete.Add(id);
        }

        foreach (var id in edges.Keys)
            Visit(id);
        return warnings;
    }
}
