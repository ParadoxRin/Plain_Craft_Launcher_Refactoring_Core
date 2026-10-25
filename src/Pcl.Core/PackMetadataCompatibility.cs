using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：EmbeddedPackMetadata 汇集 Entry、Provider、Component、Version，供该模块传递状态。
public sealed record EmbeddedPackMetadata(string Entry, string Provider, string Component, string Version);
/// <summary>Imported launcher definitions must match an official definition before rebuilding for Mac.</summary>
public static class PackMetadataCompatibility
{
    private static readonly HashSet<string> Components = [
        "net.minecraft",
        "net.fabricmc.fabric-loader",
        "org.quiltmc.quilt-loader",
        "net.minecraftforge",
        "net.neoforged",
        "org.lwjgl",
        "org.lwjgl3"
    ];
    // [实现] Read 的实现：按 entry.Length > 16 * 1024 * 1024 分支处理；准备 var r = new StreamReader( entry.Open() )；返回 JObject.Parse( r.ReadToEnd() )。
    internal static JObject Read(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("包内版本清单过大。");
        using var r = new StreamReader(
            entry.Open()
        );
        return JObject.Parse(
            r.ReadToEnd()
        );
    }

    // [实现] Equivalent 的实现：执行 JObject Normalize(JObject input) { var result = (JObject)input.DeepClone(); foreach (var key in…；按 imported["jar"] is not null 分支处理；返回 JToken.DeepEquals( Normalize(imported), Normalize(official) )。
    public static bool Equivalent(JObject imported, JObject official, string provider)
    {
        // [实现] Normalize 的实现：准备 var result = (JObject)input.DeepClone()；遍历 provider == "multimc" ? new[] { "name", "releaseTime", "time" } : new[] { "id", "releaseTime", … 处理 key；返回 result。
        JObject Normalize(JObject input)
        {
            var result = (JObject)input.DeepClone();
            foreach (var key in provider == "multimc"
                ? new[]
                {
                    "name",
                    "releaseTime",
                    "time"
                }

                : new[]
                {
                    "id",
                    "releaseTime",
                    "time"
                }

            )
                result.Remove(key);
            return result;
        }

        if (imported["jar"] is not null)
        {
            // [实现] 条件成立（imported["jar"] is not null）时：按 (string? )imported["jar"] != (string? )official["id"] && (string? )imported["jar"] != (string? … 分支处理；执行 imported = (JObject)imported.DeepClone()；执行 imported.Remove("jar")。
            if ((string? )imported["jar"] != (string? )official["id"] && (string? )imported["jar"] != (string? )official["inheritsFrom"])
                return false;
            imported = (JObject)imported.DeepClone();
            imported.Remove("jar");
        }

        return JToken.DeepEquals(
            Normalize(imported),
            Normalize(official)
        );
    }

    // [实现] MultiMc 的实现：准备 var data = Read(patch)；准备 var uid = (string? )data["uid"] ?? ""；准备 var version = (string? )data["version"] ?? ""。
    public static EmbeddedPackMetadata MultiMc(ZipArchiveEntry patch, JArray components)
    {
        var data = Read(patch);
        var uid = (string? )data["uid"] ?? "";
        var version = (string? )data["version"] ?? "";
        if (components.Count(
            x => (string? )x["uid"] == uid
        ) != 1 || components.Single(
            x => (string? )x["uid"] == uid
        )["version"]?.ToString() != version)
            throw new NotSupportedException("自定义补丁无法映射到官方组件：" + patch.FullName);
        VersionCatalog.ValidateId(version);
        if (!Components.Contains(uid))
        {
            // [实现] 条件成立（!Components.Contains(uid)）时：执行 _ = CustomPackMetadata.ReadCustomPatch(data, components, patch.FullName)；返回 new(patch.FullName, "custom-multimc", uid, version)。
            _ = CustomPackMetadata.ReadCustomPatch(data, components, patch.FullName);
            return new(patch.FullName, "custom-multimc", uid, version);
        }

        return new(patch.FullName, "multimc", uid, version);
    }

    // [实现] Hmcl 的实现：准备 var json = Read(entry)；执行 loader = "vanilla"；执行 version = ""。
    public static EmbeddedPackMetadata Hmcl(ZipArchiveEntry entry, string game, out string loader, out string version)
    {
        var json = Read(entry);
        loader = "vanilla";
        version = "";
        var main = (string? )json["mainClass"];
        if (json["inheritsFrom"] is null && main == "net.minecraft.client.main.Main")
            return new(entry.FullName, "mojang", "net.minecraft", game);
        if (json["inheritsFrom"] is not null && (string? )json["inheritsFrom"] != game)
            throw new NotSupportedException("HMCL 继承版本与整合包声明不一致。");
        var coordinates = (json["libraries"] ?? new JArray()).Select(
            x => (string? )x["name"] ?? ""
        )
            .ToArray();
        var hasFabric = coordinates.Any(
            x => x.StartsWith("net.fabricmc:fabric-loader:", StringComparison.Ordinal)
        );
        var hasQuilt = coordinates.Any(
            x => x.StartsWith("org.quiltmc:quilt-loader:", StringComparison.Ordinal)
        );
        if (hasFabric && hasQuilt)
        {
            throw new InvalidDataException("HMCL加载器身份不唯一。");
        }

        if (!hasFabric && !hasQuilt)
        {
            CustomPackMetadata.ValidateClass(main ?? "");
            return new(entry.FullName, "mojang", "net.minecraft", game);
        }

        loader = hasQuilt
            ? "quilt"
            : "fabric";
        var prefix = loader == "fabric"
            ? "net.fabricmc:fabric-loader:"
            : "org.quiltmc:quilt-loader:";
        var matches = (json["libraries"] ?? new JArray()).Select(
            x => (string? )x["name"] ?? ""
        )
            .Where(
                x => x.StartsWith(prefix, StringComparison.Ordinal)
            )
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("HMCL 加载器身份不唯一。");
        version = matches[0][prefix.Length..];
        VersionCatalog.ValidateId(version);
        return new(
            entry.FullName,
            json["inheritsFrom"] is null
                ? loader + "-merged"
                : loader,
            game,
            version
        );
    }

    // [实现] VerifyAsync 的实现：按 plan.EmbeddedMetadata.Count == 0 分支处理；准备 var libraries = new List<JObject>()；准备 var arguments = new List<string>()。
    public static async Task<CustomPackChanges> VerifyAsync(MinecraftService service, string archive, PortablePackPlan plan, CancellationToken ct)
    {
        if (plan.EmbeddedMetadata.Count == 0)
            return new(
                [],
                [],
                []
            );
        var libraries = new List<JObject>();
        var arguments = new List<string>();
        var sources = new List<string>();
        string? mainClass = null;
        var tweakers = new List<string>();
        using var zip = ZipFile.OpenRead(archive);
        foreach (var check in plan.EmbeddedMetadata.OrderBy(
            x => x.Provider == "custom-multimc"
                ? (int? )Read(
                    zip.GetEntry(x.Entry)!
                )["order"] ?? 100
                : 0
        ))
        {
            // [实现] 对 plan.EmbeddedMetadata.OrderBy( x => x.Provider == "custom-multimc" ? (int? )Read( zip.GetEntry(… 中的每一项：准备 var imported = Read( zip.GetEntry(check.Entry) ?? throw new InvalidDataException("包内版本定义已改变。") …；按 check.Provider == "custom-multimc" 分支处理；准备 JObject expected。
            var imported = Read(
                zip.GetEntry(check.Entry) ?? throw new InvalidDataException("包内版本定义已改变。")
            );
            if (check.Provider == "custom-multimc")
            {
                // [实现] 条件成立（check.Provider == "custom-multimc"）时：准备 var manifest = zip.Entries.Single( x => Path.GetFileName(x.FullName) == "mmc-pack.json" )；准备 var components = (JArray)Read(manifest)["components"]!；准备 var extra = CustomPackMetadata.ReadCustomPatch(imported, components, check.Entry)。
                var manifest = zip.Entries.Single(
                    x => Path.GetFileName(x.FullName) == "mmc-pack.json"
                );
                var components = (JArray)Read(manifest)["components"]!;
                var extra = CustomPackMetadata.ReadCustomPatch(imported, components, check.Entry);
                libraries.AddRange(extra.Libraries);
                arguments.AddRange(extra.JvmArguments);
                sources.Add(check.Entry);
                if (extra.MainClass is not null)
                {
                    mainClass = extra.MainClass;
                }

                tweakers.AddRange(
                    extra.Tweakers ?? []
                );
                continue;
            }

            JObject expected;
            var cache = Path.Combine(
                service.Paths.Root,
                "cache/pack-metadata",
                check.Provider + "-" + check.Component + "-" + check.Version + ".json"
            );
            if (check.Provider == "mojang")
            {
                // [实现] 条件成立（check.Provider == "mojang"）时：准备 var manifest = await service.GetMetadataAsync( MinecraftService.ManifestUrl, Path.Combine(servi…；准备 var release = manifest["versions"]!.Single( x => (string? )x["id"] == check.Version )；执行 expected = await service.GetMetadataAsync( (string)release["url"]!, cache, (string)release["sha…。
                var manifest = await service.GetMetadataAsync(
                    MinecraftService.ManifestUrl,
                    Path.Combine(service.Paths.Root, "cache/version_manifest_v2.json"),
                    cancellationToken: ct
                );
                var release = manifest["versions"]!.Single(
                    x => (string? )x["id"] == check.Version
                );
                expected = await service.GetMetadataAsync(
                    (string)release["url"]!,
                    cache,
                    (string)release["sha1"]!,
                    cancellationToken: ct
                );
            }
            else
            {
                // [实现] 前一条件不成立时：准备 var provider = check.Provider.Replace("-merged", "")；准备 var url = provider == "multimc" ? $"https://meta.multimc.org/v1/{check.Component}/{check.Versio…；执行 expected = await service.GetMetadataAsync(url, cache, cancellationToken: ct)。
                var provider = check.Provider.Replace("-merged", "");
                var url = provider == "multimc"
                    ? $"https://meta.multimc.org/v1/{check.Component}/{check.Version}.json"
                    : $"https://meta.{(provider == "fabric"
                            ? "fabricmc.net/v2"
                            : "quiltmc.org/v3")}/versions/loader/{check.Component}/{check.Version}/profile/json";
                expected = await service.GetMetadataAsync(url, cache, cancellationToken: ct);
                if (check.Provider.EndsWith("-merged", StringComparison.Ordinal))
                {
                    // [实现] 条件成立（check.Provider.EndsWith("-merged", StringComparison.Ordinal)）时：准备 var manifest = await service.GetMetadataAsync( MinecraftService.ManifestUrl, Path.Combine(servi…；准备 var release = manifest["versions"]!.Single( x => (string? )x["id"] == check.Component )；准备 var parent = await service.GetMetadataAsync( (string)release["url"]!, cache + ".parent.json", (…。
                    var manifest = await service.GetMetadataAsync(
                        MinecraftService.ManifestUrl,
                        Path.Combine(service.Paths.Root, "cache/version_manifest_v2.json"),
                        cancellationToken: ct
                    );
                    var release = manifest["versions"]!.Single(
                        x => (string? )x["id"] == check.Component
                    );
                    var parent = await service.GetMetadataAsync(
                        (string)release["url"]!,
                        cache + ".parent.json",
                        (string)release["sha1"]!,
                        cancellationToken: ct
                    );
                    expected = FabricService.Merge(parent, expected);
                    expected["id"] = check.Component;
                }
            }

            var additions = CustomPackMetadata.Additions(imported, expected, check.Provider, check.Entry);
            libraries.AddRange(additions.Libraries);
            arguments.AddRange(additions.JvmArguments);
            sources.AddRange(additions.Sources);
            if (additions.MainClass is not null)
            {
                mainClass = additions.MainClass;
            }

            tweakers.AddRange(
                additions.Tweakers ?? []
            );
        }

        return new(
            libraries,
            arguments,
            sources,
            mainClass,
            tweakers.Distinct(StringComparer.Ordinal)
                .ToArray()
        );
    }
}
