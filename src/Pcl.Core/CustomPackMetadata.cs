using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：CustomPackChanges 汇集 Libraries、JvmArguments、Sources，供该模块传递状态。
public sealed record CustomPackChanges(
    IReadOnlyList<JObject> Libraries,
    IReadOnlyList<string> JvmArguments,
    IReadOnlyList<string> Sources,
    string? MainClass = null,
    IReadOnlyList<string>? Tweakers = null
);
/// <summary>导入可校验的组件扩展；账户参数和Mac原生库仍沿用官方基础版本，自定义入口必须存在于校验库中。</summary>
public static class CustomPackMetadata
{
    public const string EntryName = "pcl-custom-components.json";
    /// <summary>入口只接受Java二进制类名，不能把参数、路径或占位符解释为启动指令。</summary>
    public static string ValidateClass(string value)
    {
        if (value.Length > 512 || !Regex.IsMatch(value, @"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)+$"))
        {
            throw new InvalidDataException("自定义Java类名无效。");
        }

        return value;
    }

    // [实现] ValidateArgument 的实现：按 value.Length > 2048 || !Regex.IsMatch(value, @"^-D(?:pack|modpack)\.[A-Za-z0-9_.-]+=[^\r\n\0]*$… 分支处理。
    public static void ValidateArgument(string value)
    {
        if (value.Length > 2048 || !Regex.IsMatch(value, @"^-D(?:pack|modpack)\.[A-Za-z0-9_.-]+=[^\r\n\0]*$") || value.Contains("${", StringComparison.Ordinal))
            throw new NotSupportedException("自定义参数目前仅支持 -Dpack.* / -Dmodpack.* 属性，不能覆盖入口、账户、Java 或原生库：" + value);
    }

    // [实现] ValidateLibrary 的实现：准备 var name = (string? )library["name"] ?? throw new InvalidDataException("自定义库缺少 Maven 坐标。")；准备 var expected = FabricService.MavenPath(name)；按 library.Properties() .Any( x => x.Name is not ("name" or "downloads" or "rules" or "url" or "sh… 分支处理。
    public static JObject ValidateLibrary(JObject library)
    {
        var name = (string? )library["name"] ?? throw new InvalidDataException("自定义库缺少 Maven 坐标。");
        var expected = FabricService.MavenPath(name);
        if (library.Properties()
            .Any(
                x => x.Name is not ("name" or "downloads" or "rules" or "url" or "sha1" or "size")
            ))
            throw new NotSupportedException("自定义库含不支持的原生库/解压/注入规则：" + name);
        JObject artifact;
        if (library["downloads"] is JObject downloads)
        {
            // [实现] 条件成立（library["downloads"] is JObject downloads）时：按 downloads.Count != 1 || downloads["artifact"] is not JObject a 分支处理；执行 artifact = (JObject)a.DeepClone()。
            if (downloads.Count != 1 || downloads["artifact"] is not JObject a)
                throw new NotSupportedException("自定义库暂只支持普通 artifact。");
            artifact = (JObject)a.DeepClone();
        }
        else
        {
            // [实现] 前一条件不成立时：准备 var baseUrl = (string? )library["url"] ?? throw new InvalidDataException("自定义库缺少 HTTPS 下载地址。")；执行 artifact = new() { ["path"] = expected, ["url"] = new Uri( new Uri( baseUrl.EndsWith('/') ? bas…。
            var baseUrl = (string? )library["url"] ?? throw new InvalidDataException("自定义库缺少 HTTPS 下载地址。");
            artifact = new()
            {
                ["path"] = expected,
                ["url"] = new Uri(
                    new Uri(
                        baseUrl.EndsWith('/')
                            ? baseUrl
                            : baseUrl + "/"
                    ),
                    expected
                ).AbsoluteUri,
                ["sha1"] = library["sha1"]?.DeepClone(),
                ["size"] = library["size"]?.DeepClone()
            };
        }

        if ((string? )artifact["path"] != expected || (string? )artifact["sha1"] is not { } hash || !Regex.IsMatch(hash, "^[0-9a-fA-F]{40}$") || artifact["size"]?.Type != JTokenType.Integer || (long)artifact["size"]!is < 0 or > 536870912)
            throw new InvalidDataException("自定义库必须提供匹配坐标的路径、SHA-1 和大小：" + name);
        if (!Uri.TryCreate((string? )artifact["url"], UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length > 0)
            throw new InvalidDataException("自定义库需要普通 HTTPS 地址。");
        if (library["rules"] is { } rules)
        {
            // [实现] 条件成立（library["rules"] is { } rules）时：按 rules is not JArray array 分支处理；遍历 array 处理 rule。
            if (rules is not JArray array)
                throw new InvalidDataException("库规则无效。");
            foreach (var rule in array)
            {
                // [实现] 对 array 中的每一项：按 rule is not JObject r || r.Properties() .Any( x => x.Name is not ("action" or "os") ) || (strin… 分支处理；按 r["os"] is { } os && (os is not JObject o || o.Properties() .Any( x => x.Name is not ("name" or… 分支处理。
                if (rule is not JObject r || r.Properties()
                    .Any(
                        x => x.Name is not ("action" or "os")
                    ) || (string? )r["action"] is not ("allow" or "disallow"))
                    throw new NotSupportedException("自定义库只支持标准操作系统规则。");
                if (r["os"] is { } os && (os is not JObject o || o.Properties()
                    .Any(
                        x => x.Name is not ("name" or "arch" or "version") || x.Value.Type != JTokenType.String
                    )))
                    throw new NotSupportedException("自定义库操作系统规则无效。");
            }
        }

        artifact["sha1"] = hash.ToLowerInvariant();
        var normalized = new JObject
        {
            ["name"] = name,
            ["downloads"] = new JObject
            {
                ["artifact"] = artifact
            }
        };
        if (library["rules"] is not null)
            normalized["rules"] = library["rules"]!.DeepClone();
        return normalized;
    }

    // [实现] ReadCustomPatch 的实现：准备 var allowed = new HashSet<string> { "uid", "version", "name", "formatVersion", "order", "librar…；按 data.Properties() .Any( x => !allowed.Contains(x.Name) ) 分支处理；按 (int? )data["formatVersion"] != 1 || (int? )data["order"] is not >= 100 分支处理。
    public static CustomPackChanges ReadCustomPatch(JObject data, JArray components, string source)
    {
        var allowed = new HashSet<string>
        {
            "uid",
            "version",
            "name",
            "formatVersion",
            "order",
            "libraries",
            "+libraries",
            "requires",
            "conflicts",
            "releaseTime",
            "type",
            "mainClass",
            "+tweakers"
        };
        if (data.Properties()
            .Any(
                x => !allowed.Contains(x.Name)
            ))
            throw new NotSupportedException("自定义 MultiMC 补丁包含不支持的游戏参数或未知操作：" + source);
        if ((int? )data["formatVersion"] != 1 || (int? )data["order"] is not >= 100)
            throw new NotSupportedException("自定义 MultiMC 补丁需 formatVersion 1，且 order 至少为100，排在标准组件之后。");
        foreach (var field in new[]
        {
            "requires",
            "conflicts"
        }

        )
            if (data[field] is { } value)
            {
                // [实现] 条件成立（data[field] is { } value）时：按 value is not JArray rows 分支处理；遍历 rows 处理 row。
                if (value is not JArray rows)
                    throw new InvalidDataException("自定义组件关系必须是数组。");
                foreach (var row in rows)
                {
                    // [实现] 对 rows 中的每一项：按 row is not JObject relation || relation.Properties() .Any( x => x.Name is not ("uid" or "equals… 分支处理；准备 var match = components.Any( x => (string? )x["uid"] == uid && (relation["equals"] is null || (s…；按 field == "requires" && !match || field == "conflicts" && match 分支处理。
                    if (row is not JObject relation || relation.Properties()
                        .Any(
                            x => x.Name is not ("uid" or "equals")
                        ) || (string? )relation["uid"] is not { } uid)
                        throw new NotSupportedException("自定义组件仅支持 uid / equals 依赖关系。");
                    var match = components.Any(
                        x => (string? )x["uid"] == uid && (relation["equals"] is null || (string? )x["version"] == (string? )relation["equals"])
                    );
                    if (field == "requires" && !match || field == "conflicts" && match)
                        throw new InvalidDataException("自定义组件依赖/冲突不满足：" + uid);
                }
            }

        if (data["libraries"] is not null && data["+libraries"] is not null)
            throw new InvalidDataException("MultiMC 不支持同时声明 libraries 与 +libraries。");
        var libraries = new List<JObject>();
        foreach (var field in new[]
        {
            "libraries",
            "+libraries"
        }

        )
            if (data[field] is { } value)
            {
                // [实现] 条件成立（data[field] is { } value）时：按 value is not JArray array 分支处理；遍历 array 处理 lib。
                if (value is not JArray array)
                    throw new InvalidDataException("自定义库必须是数组。");
                foreach (var lib in array)
                    libraries.Add(
                        ValidateLibrary(
                            lib as JObject ?? throw new InvalidDataException("自定义库格式无效。")
                        )
                    );
            }

        var main = data["mainClass"] is null
            ? null
            : ValidateClass((string? )data["mainClass"] ?? "");
        var tweakers = ReadTweakers(data["+tweakers"]);
        return new(
            libraries,
            [],
            [
                source
            ],
            main,
            tweakers
        );
    }

    // [实现] Additions 的实现：准备 var copy = (JObject)imported.DeepClone()；准备 var extras = new List<JObject>()；按 copy["libraries"] is JArray libs && official["libraries"] is JArray expected 分支处理。
    public static CustomPackChanges Additions(JObject imported, JObject official, string provider, string source)
    {
        var copy = (JObject)imported.DeepClone();
        var extras = new List<JObject>();
        if (copy["libraries"] is JArray libs && official["libraries"] is JArray expected)
        {
            // [实现] 条件成立（copy["libraries"] is JArray libs && official["libraries"] is JArray expected）时：准备 var names = expected.Select( x => (string? )x["name"] ) .ToHashSet()；遍历 libs.OfType<JObject>() .ToArray() 处理 library。
            var names = expected.Select(
                x => (string? )x["name"]
            )
                .ToHashSet();
            foreach (var library in libs.OfType<JObject>()
                .ToArray())
                if (!names.Contains((string? )library["name"]))
                {
                    // [实现] 条件成立（!names.Contains((string? )library["name"])）时：执行 extras.Add( ValidateLibrary(library) )；执行 library.Remove()。
                    extras.Add(
                        ValidateLibrary(library)
                    );
                    library.Remove();
                }
        }

        var args = new List<string>();
        if (copy["arguments"]?["jvm"] is JArray jvm && official["arguments"]?["jvm"] is JArray expectedJvm)
        {
            // [实现] 条件成立（copy["arguments"]?["jvm"] is JArray jvm && official["arguments"]?["jvm"] is JArray expectedJvm）时：在 jvm.Count > expectedJvm.Count 条件下循环。
            while (jvm.Count > expectedJvm.Count)
            {
                // [实现] 循环满足 jvm.Count > expectedJvm.Count 时：准备 var arg = jvm[expectedJvm.Count]；按 arg.Type != JTokenType.String 分支处理；执行 ValidateArgument((string)arg!)。
                var arg = jvm[expectedJvm.Count];
                if (arg.Type != JTokenType.String)
                    throw new NotSupportedException("自定义 JVM 条件参数暂不支持。");
                ValidateArgument((string)arg!);
                args.Add((string)arg!);
                arg.Remove();
            }
        }

        string? main = null;
        if (!JToken.DeepEquals(copy["mainClass"], official["mainClass"]))
        {
            main = ValidateClass((string? )copy["mainClass"] ?? "");
            copy["mainClass"] = official["mainClass"]?.DeepClone();
        }

        if (!PackMetadataCompatibility.Equivalent(copy, official, provider))
            throw new NotSupportedException("包内定义修改了标准组件；仅支持校验库中的入口、额外库与 pack/modpack 属性：" + source);
        return new(
            extras,
            args,
            [
                source
            ],
            main
        );
    }

    // [实现] Serialize 的实现：计算并返回 new() { ["schemaVersion"] = changes.MainClass is null && (changes.Tweakers?.Count ?? 0) == 0 ? 1 : 2, ["libraries"] = new JArray(changes.Libraries), ["jvmArguments"] …。
    public static JObject Serialize(CustomPackChanges changes) => new()
    {
        ["schemaVersion"] = changes.MainClass is null && (changes.Tweakers?.Count ?? 0) == 0
            ? 1
            : 2,
        ["libraries"] = new JArray(changes.Libraries),
        ["jvmArguments"] = new JArray(changes.JvmArguments),
        ["sources"] = new JArray(changes.Sources),
        ["mainClass"] = changes.MainClass,
        ["tweakers"] = new JArray(
            changes.Tweakers ?? []
        )
    };
    // [实现] ReadArchive 的实现：准备 var zip = ZipFile.OpenRead(archive)；准备 var entries = zip.Entries.Where( x => x.FullName == EntryName ) .ToArray()；按 entries.Length == 0 分支处理。
    public static CustomPackChanges ReadArchive(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.Where(
            x => x.FullName == EntryName
        )
            .ToArray();
        if (entries.Length == 0)
            return new(
                [],
                [],
                []
            );
        if (entries.Length != 1)
            throw new InvalidDataException("自定义组件清单重复。");
        return Parse(
            PackMetadataCompatibility.Read(entries[0])
        );
    }

    // [实现] Parse 的实现：按 (int? )json["schemaVersion"] != 1 || json["libraries"] is not JArray libs || json["jvmArguments… 分支处理；准备 var parsed = libs.Select( x => ValidateLibrary( x as JObject ?? throw new InvalidDataException(…；准备 var arguments = args.Select( x => x.Type == JTokenType.String ? (string)x! : throw new InvalidD…。
    public static CustomPackChanges Parse(JObject json)
    {
        if ((int? )json["schemaVersion"] is not (1 or 2) || json["libraries"] is not JArray libs || json["jvmArguments"] is not JArray args || libs.Count > 256 || args.Count > 128)
            throw new InvalidDataException("自定义组件清单无效或过大。");
        var parsed = libs.Select(
            x => ValidateLibrary(
                x as JObject ?? throw new InvalidDataException("自定义库无效。")
            )
        )
            .ToArray();
        var arguments = args.Select(
            x => x.Type == JTokenType.String
                ? (string)x!
                : throw new InvalidDataException("参数无效。")
        )
            .ToArray();
        foreach (var arg in arguments)
            ValidateArgument(arg);
        var main = json["mainClass"]?.Type is null or JTokenType.Null
            ? null
            : ValidateClass((string? )json["mainClass"] ?? "");
        var tweakers = ReadTweakers(json["tweakers"]);
        if ((int? )json["schemaVersion"] == 1 && (main is not null || tweakers.Count > 0))
        {
            throw new InvalidDataException("自定义启动入口需要扩展清单版本2。");
        }

        return new(
            parsed,
            arguments,
            [],
            main,
            tweakers
        );
    }

    /// <summary>保留MultiMC追加tweaker的顺序；重复类名去重，未知形状不静默丢失。</summary>
    private static IReadOnlyList<string> ReadTweakers(JToken? value)
    {
        if (value is null || value.Type == JTokenType.Null)
        {
            return[];
        }

        if (value is not JArray array || array.Count > 64)
        {
            throw new InvalidDataException("自定义tweakers必须是最多64项的数组。");
        }

        return array.Select(
            x => x.Type == JTokenType.String
                ? ValidateClass((string)x!)
                : throw new InvalidDataException("tweaker必须是类名。")
        )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    // [实现] ApplyAsync 的实现：按 changes.Libraries.Count == 0 && changes.JvmArguments.Count == 0 分支处理；准备 var metadata = JsonFiles.Read( Path.Combine(instance, "version.json") )；准备 var libraries = (JArray)metadata["libraries"]!。
    public static async Task ApplyAsync(MinecraftService service, string instance, CustomPackChanges changes, CancellationToken ct)
    {
        if (changes.Libraries.Count == 0 && changes.JvmArguments.Count == 0 && changes.MainClass is null && (changes.Tweakers?.Count ?? 0) == 0)
            return;
        var metadata = JsonFiles.Read(
            Path.Combine(instance, "version.json")
        );
        var libraries = (JArray)metadata["libraries"]!;
        // [实现] Key 的实现：准备 var p = name.Split(':')；返回 string.Join( ':', p.Take(2) ) + (p.Length == 4 ? ":" + p[3] : "")。
        string Key(string name)
        {
            var p = name.Split(':');
            return string.Join(
                ':',
                p.Take(2)
            ) + (p.Length == 4
                ? ":" + p[3]
                : "");
        }

        var keys = libraries.Select(
            x => Key((string)x["name"]!)
        )
            .ToHashSet(StringComparer.Ordinal);
        foreach (var supplied in changes.Libraries)
        {
            // [实现] 对 changes.Libraries 中的每一项：准备 var lib = ValidateLibrary(supplied)；准备 var name = (string)lib["name"]!；按 !keys.Add( Key(name) ) 分支处理。
            var lib = ValidateLibrary(supplied);
            var name = (string)lib["name"]!;
            if (!keys.Add(
                Key(name)
            ))
                throw new NotSupportedException("自定义库不能替换标准或已声明的库：" + name);
            var artifact = (JObject)lib["downloads"]!["artifact"]!;
            artifact["path"] = "pcl-custom/" + (string)artifact["sha1"]! + "/" + (string)artifact["path"]!;
            libraries.Add(lib);
        }

        foreach (var arg in changes.JvmArguments)
        {
            // [实现] 对 changes.JvmArguments 中的每一项：执行 ValidateArgument(arg)；执行 ((JArray)metadata["arguments"]!["jvm"]!).Add(arg)。
            ValidateArgument(arg);
            ((JArray)metadata["arguments"]!["jvm"]!).Add(arg);
        }

        foreach (var item in MinecraftService.BuildLibraries(
            metadata,
            File.Exists(
                Path.Combine(instance, "profile.json")
            ) && (string? )JsonFiles.Read(
                Path.Combine(instance, "profile.json")
            )["libraryRoot"] == "instance"
                ? instance
                : service.Paths.Game,
            service.Platform
        )
            .Where(
                x => x.Path.Contains(
                    Path.DirectorySeparatorChar + "pcl-custom" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            ))
            await service.Downloads.DownloadAsync(item, ct);
        // 下载校验成功后检查实际Mac类路径，入口不存在或被多份库抢占时拒绝发布实例。
        var libraryRoot = File.Exists(
            Path.Combine(instance, "profile.json")
        ) && (string? )JsonFiles.Read(
            Path.Combine(instance, "profile.json")
        )["libraryRoot"] == "instance"
            ? instance
            : service.Paths.Game;
        var classFiles = MinecraftService.BuildLibraries(metadata, libraryRoot, service.Platform)
            .Where(
                x => x.Path.Contains(
                    Path.DirectorySeparatorChar + "pcl-custom" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            )
            .ToArray();
        foreach (var className in new[]
        {
            changes.MainClass
        }.Concat(
            changes.Tweakers ?? []
        )
            .OfType<string>())
        {
            ct.ThrowIfCancellationRequested();
            var entry = ValidateClass(className)
                .Replace('.', '/') + ".class";
            var count = 0;
            foreach (var library in classFiles)
            {
                using var archive = ZipFile.OpenRead(library.Path);
                count += archive.Entries.Count(
                    x => x.FullName == entry
                );
            }

            if (count != 1)
            {
                throw new InvalidDataException("自定义入口必须唯一存在于当前Mac适用的校验库中：" + className);
            }
        }

        if (changes.MainClass is not null)
        {
            metadata["mainClass"] = changes.MainClass;
        }

        if ((changes.Tweakers?.Count ?? 0) > 0)
        {
            if ((string? )metadata["mainClass"] != "net.minecraft.launchwrapper.Launch")
            {
                throw new NotSupportedException("MultiMC +tweakers 仅适用于LaunchWrapper入口。");
            }

            foreach (var tweaker in changes.Tweakers!)
            {
                ((JArray)metadata["arguments"]!["game"]!).Add("--tweakClass");
                ((JArray)metadata["arguments"]!["game"]!).Add(tweaker);
            }
        }

        JsonFiles.Write(
            Path.Combine(instance, "version.json"),
            metadata
        );
        JsonFiles.Write(
            Path.Combine(instance, "pcl-custom-components.json"),
            Serialize(changes)
        );
    }
}
