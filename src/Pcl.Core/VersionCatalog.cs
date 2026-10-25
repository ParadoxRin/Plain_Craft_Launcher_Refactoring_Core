using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：MinecraftRelease 汇集 Id、Type、Released、Url、Sha1，供该模块传递状态。
public sealed record MinecraftRelease(string Id, string Type, DateTimeOffset Released, string Url, string Sha1)
{
    // [实现] ToString 的实现：计算并返回 Id + (Type == "release" ? "" : " · " + Type)。
    public override string ToString() => Id + (Type == "release"
        ? ""
        : " · " + Type);
}

// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public static class VersionCatalog
{
    // [实现] ValidateId 的实现：按 !Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$") || id.Contains("..") 分支处理。
    public static void ValidateId(string id) => Pcl.Core.Domain.GameVersionIdentity.Validate(id);

    // [实现] ListAsync 的实现：准备 var manifest = await service.GetMetadataAsync( MinecraftService.ManifestUrl, Path.Combine(servi…；返回 manifest["versions"]!.Select( x => new MinecraftRelease( (string)x["id"]!, (string)x["type"]!, …。
    public static async Task<IReadOnlyList<MinecraftRelease>> ListAsync(MinecraftService service, CancellationToken ct = default)
    {
        var manifest = await service.GetMetadataAsync(
            MinecraftService.ManifestUrl,
            Path.Combine(service.Paths.Root, "cache/version_manifest_v2.json"),
            cancellationToken: ct
        );
        return manifest["versions"]!.Select(
            x => new MinecraftRelease(
                (string)x["id"]!,
                (string)x["type"]!,
                (DateTimeOffset)x["releaseTime"]!,
                (string)x["url"]!,
                (string)x["sha1"]!
            )
        )
            .ToArray();
    }

    // [实现] JavaMajor 的实现：计算并返回 (int? )metadata["javaVersion"]?["majorVersion"] ?? 8。
    public static int JavaMajor(JObject metadata) => (int? )metadata["javaVersion"]?["majorVersion"] ?? 8;
    // [实现] NeoForgePrefix 的实现：准备 var p = minecraft.Split('.')；按 p.Length >= 2 && p[0] == "1" && int.TryParse(p[1], out var minor) && minor >= 20 分支处理；返回 minecraft + "."。
    public static string NeoForgePrefix(string minecraft)
    {
        var p = minecraft.Split('.');
        if (p.Length >= 2 && p[0] == "1" && int.TryParse(p[1], out var minor) && minor >= 20)
            return p[1] + "." + (p.Length > 2
                ? p[2]
                : "0") + ".";
        return minecraft + ".";
    }
}
