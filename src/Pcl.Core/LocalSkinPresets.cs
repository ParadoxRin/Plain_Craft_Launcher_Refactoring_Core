using System.IO.Compression;

namespace Pcl.Core;
/// <summary>从用户已安装的原版客户端读取默认皮肤；不捆绑游戏材质；返回纹理及手臂模型，启动层据此选择离线 UUID。</summary>
public static class LocalSkinPresets
{
    // [实现] Read 的实现：按 preset is not ("steve" or "alex" or "random") 分支处理；准备 var folder = Path.Combine(paths.Game, "versions")；按 !Directory.Exists(folder) 分支处理。
    public static byte[] Read(AppPaths paths, string preset) => ReadWithModel(paths, preset)
        .Png;
    public static (byte[] Png, string Model) ReadWithModel(AppPaths paths, string preset)
    {
        if (preset is not ("steve" or "alex" or "random"))
        {
            // [实现] 条件成立（preset is not ("steve" or "alex" or "random")）时：抛出 new InvalidDataException("未知默认皮肤。")。
            throw new InvalidDataException("未知默认皮肤。");
        }

        var folder = Path.Combine(paths.Game, "versions");
        if (!Directory.Exists(folder))
        {
            // [实现] 条件成立（!Directory.Exists(folder)）时：抛出 new InvalidOperationException("请先安装一个 Minecraft 版本，再选择内置皮肤。")。
            throw new InvalidOperationException("请先安装一个 Minecraft 版本，再选择内置皮肤。");
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.jar", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            // [实现] 对 Directory.EnumerateFiles(folder, "*.jar", SearchOption.AllDirectories) .OrderByDescending(File.… 中的每一项：准备 var zip = ZipFile.OpenRead(file)；准备 var entries = zip.Entries.Where( x => x.FullName.StartsWith("assets/minecraft/textures/entity/p…；按 entries.Length == 0 分支处理。
            using var zip = ZipFile.OpenRead(file);
            var entries = zip.Entries.Where(
                x => x.FullName.StartsWith("assets/minecraft/textures/entity/player/", StringComparison.Ordinal) && x.FullName.EndsWith(".png", StringComparison.Ordinal) || x.FullName is "assets/minecraft/textures/entity/steve.png" or "assets/minecraft/textures/entity/alex.png"
            )
                .Where(
                    x => preset == "random" || Path.GetFileNameWithoutExtension(x.FullName) == preset
                )
                .ToArray();
            if (entries.Length == 0)
            {
                // [实现] 条件成立（entries.Length == 0）时：执行 continue;。
                continue;
            }

            var entry = preset == "random"
                ? entries[Random.Shared.Next(entries.Length)]
                : entries.FirstOrDefault(
                    x => x.FullName.Contains(
                        preset == "alex"
                            ? "/slim/"
                            : "/wide/"
                    )
                ) ?? entries[0];
            if (entry.Length > 1024 * 1024)
            {
                // [实现] 条件成立（entry.Length > 1024 * 1024）时：抛出 new InvalidDataException("默认皮肤图片过大。")。
                throw new InvalidDataException("默认皮肤图片过大。");
            }

            using var input = entry.Open();
            using var data = new MemoryStream();
            input.CopyTo(data);
            var bytes = data.ToArray();
            ExternalAuthentication.ValidatePng(bytes, "skin");
            return (bytes, entry.FullName.Contains("/slim/") || entry.FullName.EndsWith("/alex.png") && !entry.FullName.Contains("/wide/")
                ? "slim"
                : "wide");
        }

        throw new FileNotFoundException("已安装客户端中没有此默认皮肤；请安装 1.8 或更新版本。");
    }
}
