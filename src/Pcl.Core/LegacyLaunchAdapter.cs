using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>Normalizes Mojang's pre-1.13 launch format without invoking a shell.</summary>
public static class LegacyLaunchAdapter
{
    // [实现] NormalizeMetadata 的实现：准备 var result = (JObject)original.DeepClone()；按 result["arguments"] is JObject 分支处理；按 result["minecraftArguments"] is not JValue legacy || legacy.Type != JTokenType.String 分支处理。
    public static JObject NormalizeMetadata(JObject original)
    {
        var result = (JObject)original.DeepClone();
        if (result["arguments"] is JObject)
            return result;
        if (result["minecraftArguments"] is not JValue legacy || legacy.Type != JTokenType.String)
            throw new NotSupportedException("此版本缺少可识别的启动参数格式。");
        result["arguments"] = new JObject
        {
            ["game"] = new JArray(
                SplitArguments((string)legacy!)
            ),
            ["jvm"] = new JArray("-Djava.library.path=${natives_directory}", "-cp", "${classpath}")
        };
        result["pclLegacyArguments"] = true;
        return result;
    }

    // [实现] SplitArguments 的实现：准备 var result = new List<string>()；准备 var word = new StringBuilder()；准备 char quote = '\0'。
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int i = 0; i < text.Length; i++)
        {
            // [实现] 每次迭代（i < text.Length）：准备 var c = text[i]；按 c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\… 分支处理；按 quote != '\0' 分支处理。
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\0' && char.IsWhiteSpace(text[i + 1])))
            {
                // [实现] 条件成立（c == '\\' && i + 1 < text.Length && (text[i + 1] == quote || text[i + 1] == '\\' || quote == '\…）时：执行 word.Append(text[++i])；执行 started = true；执行 continue;。
                word.Append(text[++i]);
                started = true;
                continue;
            }

            if (quote != '\0')
            {
                // [实现] 条件成立（quote != '\0'）时：按 c == quote 分支处理；执行 started = true；执行 continue;。
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
                started = true;
                continue;
            }

            if (c is '\'' or '"')
            {
                // [实现] 条件成立（c is '\'' or '"'）时：执行 quote = c；执行 started = true；执行 continue;。
                quote = c;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // [实现] 条件成立（char.IsWhiteSpace(c)）时：按 started 分支处理。
                if (started)
                {
                    // [实现] 条件成立（started）时：执行 result.Add( word.ToString() )；执行 word.Clear()；执行 started = false。
                    result.Add(
                        word.ToString()
                    );
                    word.Clear();
                    started = false;
                }
            }
            else
            {
                // [实现] 前一条件不成立时：执行 word.Append(c)；执行 started = true。
                word.Append(c);
                started = true;
            }
        }

        if (quote != '\0')
            throw new InvalidDataException("旧版启动参数的引号未闭合。");
        if (started)
            result.Add(
                word.ToString()
            );
        return result;
    }

    // [实现] RequiresFirstThread 的实现：计算并返回 metadata["libraries"]?.Any( x => ((string? )x["name"])?.StartsWith("org.lwjgl.lwjgl:", StringCo…。
    public static bool RequiresFirstThread(JObject metadata) => metadata["libraries"]?.Any(
        x => ((string? )x["name"])?.StartsWith("org.lwjgl.lwjgl:", StringComparison.Ordinal) == true
    ) != true;
    // [实现] GameAssets 的实现：准备 var indexPath = AppPaths.SafeChild( Path.Combine(assetsRoot, "indexes"), version.AssetIndex + "…；返回 File.Exists(indexPath) && (bool? )JsonFiles.Read(indexPath)["virtual"] == true ? AppPaths.SafeC…。
    public static string GameAssets(InstalledVersion version, string assetsRoot)
    {
        var indexPath = AppPaths.SafeChild(
            Path.Combine(assetsRoot, "indexes"),
            version.AssetIndex + ".json"
        );
        return File.Exists(indexPath) && (bool? )JsonFiles.Read(indexPath)["virtual"] == true
            ? AppPaths.SafeChild(
                Path.Combine(assetsRoot, "virtual"),
                version.AssetIndex
            )
            : assetsRoot;
    }

    // [实现] PrepareFiles 的实现：准备 var nativeRoot = Path.Combine( Path.GetDirectoryName(version.JsonPath)!, "natives" )；执行 Directory.CreateDirectory(nativeRoot)；遍历 version.Libraries.Where( x => x.ExtractNative ) 处理 file。
    public static void PrepareFiles(
        InstalledVersion version,
        string assetsRoot,
        string gameDirectory,
        CancellationToken ct = default
    )
    {
        var nativeRoot = Path.Combine(
            Path.GetDirectoryName(version.JsonPath)!,
            "natives"
        );
        Directory.CreateDirectory(nativeRoot);
        foreach (var file in version.Libraries.Where(
            x => x.ExtractNative
        ))
        {
            // [实现] 对 version.Libraries.Where( x => x.ExtractNative ) 中的每一项：按 !DownloadService.IsValid(file) 分支处理；准备 var zip = ZipFile.OpenRead(file.Path)；遍历 zip.Entries 处理 entry。
            if (!DownloadService.IsValid(file))
                throw new InvalidDataException("原生库压缩包校验失败：" + file.Path);
            using var zip = ZipFile.OpenRead(file.Path);
            foreach (var entry in zip.Entries)
            {
                // [实现] 对 zip.Entries 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.Name.Length… 分支处理；按 entry.Length > 64L * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 分支处理。
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0)
                    continue;
                if (entry.Length > 64L * 1024 * 1024 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("原生库条目无效。");
                if (!entry.FullName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".jnilib", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = ResourceInstaller.SafeTarget(nativeRoot, entry.FullName);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)!
                );
                var temporary = path + "." + Guid.NewGuid()
                    .ToString("N") + ".part";
                try
                {
                    // [实现] 在异常保护范围内：在作用域内使用并释放 var output = File.Create(temporary)；执行 File.Move(temporary, path, true)。
                    using (var output = File.Create(temporary))
                    {
                        // [实现] 本段流程：准备 var input = entry.Open()；执行 input.CopyTo(output)。
                        using var input = entry.Open();
                        input.CopyTo(output);
                    }

                    File.Move(temporary, path, true);
                }
                finally
                {
                    // [实现] 无论成功或失败，退出前：按 File.Exists(temporary) 分支处理。
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }

        var indexPath = AppPaths.SafeChild(
            Path.Combine(assetsRoot, "indexes"),
            version.AssetIndex + ".json"
        );
        if (!File.Exists(indexPath))
            return;
        var index = JsonFiles.Read(indexPath);
        var targets = new List<string>();
        if ((bool? )index["virtual"] == true)
            targets.Add(
                GameAssets(version, assetsRoot)
            );
        if ((bool? )index["map_to_resources"] == true)
            targets.Add(
                Path.Combine(gameDirectory, "resources")
            );
        foreach (var asset in ((JObject)index["objects"]!).Properties())
        {
            // [实现] 对 ((JObject)index["objects"]!).Properties() 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var hash = (string)asset.Value["hash"]!；按 !Regex.IsMatch(hash, "^[0-9a-f]{40}$") 分支处理。
            ct.ThrowIfCancellationRequested();
            var hash = (string)asset.Value["hash"]!;
            if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$"))
                throw new InvalidDataException("旧版资源索引哈希无效。");
            var origin = Path.Combine(assetsRoot, "objects", hash[..2], hash);
            foreach (var targetRoot in targets)
            {
                // [实现] 对 targets 中的每一项：准备 var path = ResourceInstaller.SafeTarget(targetRoot, asset.Name)；准备 var check = new DownloadItem( new Uri("https://localhost/unused"), path, (long)asset.Value["siz…；按 DownloadService.IsValid(check) 分支处理。
                var path = ResourceInstaller.SafeTarget(targetRoot, asset.Name);
                var check = new DownloadItem(
                    new Uri("https://localhost/unused"),
                    path,
                    (long)asset.Value["size"]!,
                    hash
                );
                if (DownloadService.IsValid(check))
                    continue;
                if (!DownloadService.IsValid(
                    check with
                    {
                        Path = origin
                    }
                ))
                    throw new InvalidDataException("旧版资源对象校验失败：" + asset.Name);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)!
                );
                File.Copy(origin, path, true);
            }
        }
    }
}
