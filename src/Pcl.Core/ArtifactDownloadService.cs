using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：DownloadArtifact 汇集 Kind、Version、FileName、Url、Size、Sha1，供该模块传递状态。
public sealed record DownloadArtifact(
    string Kind,
    string Version,
    string FileName,
    string? Url,
    long Size = -1,
    string? Sha1 = null
)
{
    // [实现] ToString 的实现：计算并返回 Version + " · " + FileName。
    public override string ToString() => Version + " · " + FileName;
}

// [实现] 数据约定：SavedArtifact 汇集 Path、Source、Size、Sha256、ExpectedSha1、Verification，供该模块传递状态。
public sealed record SavedArtifact(
    string Path,
    string Source,
    long Size,
    string Sha256,
    string? ExpectedSha1,
    string Verification
);
// [实现] 数据约定：VersionNotes 汇集 Title、Text、Source，供该模块传递状态。
public sealed record VersionNotes(string Title, string Text, Uri Source);
/// <summary>Save official artifacts without installing or executing them.</summary>
public sealed class ArtifactDownloadService(MinecraftService service) : IDisposable
{
    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    public static readonly string[] Kinds = [
        "client",
        "server",
        "forge",
        "neoforge",
        "fabric",
        "quilt",
        "optifine",
        "liteloader"
    ];
    // [实现] ListAsync 的实现：按 !Kinds.Contains(kind) 分支处理；按 kind is "client" or "server" 分支处理；按 kind is "forge" or "neoforge" 分支处理。
    public async Task<IReadOnlyList<DownloadArtifact>> ListAsync(string kind, CancellationToken ct = default)
    {
        if (!Kinds.Contains(kind))
            throw new ArgumentException("下载类型无效。");
        if (kind is "client" or "server")
        {
            // [实现] 条件成立（kind is "client" or "server"）时：准备 var manifest = await service.GetMetadataAsync( MinecraftService.ManifestUrl, Path.Combine(servi…；准备 var entry = manifest["versions"]!.OfType<JObject>() .SingleOrDefault( x => (string? )x["id"] ==…；准备 var metadata = await service.GetMetadataAsync( (string)entry["url"]!, AppPaths.SafeChild( Path.…。
            var manifest = await service.GetMetadataAsync(
                MinecraftService.ManifestUrl,
                Path.Combine(service.Paths.Root, "cache/version_manifest_v2.json"),
                cancellationToken: ct
            );
            var entry = manifest["versions"]!.OfType<JObject>()
                .SingleOrDefault(
                    x => (string? )x["id"] == service.GameVersion
                ) ?? throw new InvalidDataException("官方目录没有此游戏版本。");
            var metadata = await service.GetMetadataAsync(
                (string)entry["url"]!,
                AppPaths.SafeChild(
                    Path.Combine(service.Paths.Root, "cache/download-only"),
                    service.GameVersion + ".json"
                ),
                (string)entry["sha1"]!,
                cancellationToken: ct
            );
            if (metadata["downloads"]?[kind] is not JObject file)
                return[];
            return[
                new(
                    kind,
                    service.GameVersion,
                    "minecraft-" + kind + "-" + service.GameVersion + ".jar",
                    (string)file["url"]!,
                    (long)file["size"]!,
                    (string)file["sha1"]!
                )
            ];
        }

        if (kind is "forge" or "neoforge")
        {
            // [实现] 条件成立（kind is "forge" or "neoforge"）时：准备 var forge = new ForgeService(service)；返回 (await forge.VersionsAsync(kind, ct)).Select( x => { var family = x.BaseUrl.EndsWith("/forge/",…。
            using var forge = new ForgeService(service);
            return (await forge.VersionsAsync(kind, ct)).Select(
                x =>
                {
                    // [实现] 回调触发时：准备 var family = x.BaseUrl.EndsWith("/forge/", StringComparison.Ordinal) ? "forge" : "neoforge"；准备 var name = family + "-" + x.MavenVersion + "-installer.jar"；返回 new DownloadArtifact(kind, x.Version, name, x.BaseUrl + x.MavenVersion + "/" + name)。
                    var family = x.BaseUrl.EndsWith("/forge/", StringComparison.Ordinal)
                        ? "forge"
                        : "neoforge";
                    var name = family + "-" + x.MavenVersion + "-installer.jar";
                    return new DownloadArtifact(kind, x.Version, name, x.BaseUrl + x.MavenVersion + "/" + name);
                }
            )
                .ToArray();
        }

        if (kind is "fabric" or "quilt")
        {
            // [实现] 条件成立（kind is "fabric" or "quilt"）时：准备 var url = kind == "fabric" ? "https://meta.fabricmc.net/v2/versions/installer" : "https://meta.…；准备 var entries = JArray.Parse( await http.GetStringAsync(url, ct) )；返回 entries.OfType<JObject>() .Select( x => new DownloadArtifact( kind, (string)x["version"]!, Path…。
            var url = kind == "fabric"
                ? "https://meta.fabricmc.net/v2/versions/installer"
                : "https://meta.quiltmc.org/v3/versions/installer";
            var entries = JArray.Parse(
                await http.GetStringAsync(url, ct)
            );
            return entries.OfType<JObject>()
                .Select(
                    x => new DownloadArtifact(
                        kind,
                        (string)x["version"]!,
                        Path.GetFileName(
                            new Uri((string)x["url"]!).AbsolutePath
                        ),
                        (string)x["url"]!,
                        (long? )x["file_size"] ?? -1,
                        (string? )x["hashes"]?["sha1"]
                    )
                )
                .ToArray();
        }

        if (kind == "optifine")
        {
            // [实现] 条件成立（kind == "optifine"）时：准备 var optifine = new OptiFineService(service)；返回 (await optifine.VersionsAsync(ct)).Select( x => new DownloadArtifact(kind, x.FileName, x.FileNa…。
            using var optifine = new OptiFineService(service);
            return (await optifine.VersionsAsync(ct)).Select(
                x => new DownloadArtifact(kind, x.FileName, x.FileName, null)
            )
                .ToArray();
        }

        using var lite = new LiteLoaderService(service);
        return (await lite.VersionsAsync(ct)).Select(
            x => new DownloadArtifact(kind, x.Build, "liteloader-" + x.Version + ".jar", null)
        )
            .ToArray();
    }

    // [实现] SaveAsync 的实现：按 File.Exists(destination) || Directory.Exists(destination) 分支处理；准备 DownloadItem spec；准备 var verification = "official_sha1"。
    public async Task<SavedArtifact> SaveAsync(DownloadArtifact artifact, string destination, CancellationToken ct = default)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标已经存在，请使用其他文件名。");
        DownloadItem spec;
        var verification = "official_sha1";
        if (artifact.Kind == "optifine")
        {
            // [实现] 条件成立（artifact.Kind == "optifine"）时：准备 var optifine = new OptiFineService(service)；执行 spec = await optifine.DownloadSpecAsync(artifact.Version, destination, ct)；执行 verification = spec.Sha1 is null ? "author_https_and_local_sha256_only" : "pcl2_known_sha1"。
            using var optifine = new OptiFineService(service);
            spec = await optifine.DownloadSpecAsync(artifact.Version, destination, ct);
            verification = spec.Sha1 is null
                ? "author_https_and_local_sha256_only"
                : "pcl2_known_sha1";
        }
        else if (artifact.Kind == "liteloader")
        {
            // [实现] 条件成立（artifact.Kind == "liteloader"）时：准备 var lite = new LiteLoaderService(service)；执行 spec = await lite.DownloadSpecAsync(artifact.Version, destination, ct)。
            using var lite = new LiteLoaderService(service);
            spec = await lite.DownloadSpecAsync(artifact.Version, destination, ct);
        }
        else
        {
            // [实现] 前一条件不成立时：按 !Kinds.Contains(artifact.Kind) || artifact.Url is null 分支处理；准备 var uri = OfficialUri(artifact.Url)；准备 var hash = artifact.Sha1。
            if (!Kinds.Contains(artifact.Kind) || artifact.Url is null)
                throw new InvalidDataException("下载元数据无效。");
            var uri = OfficialUri(artifact.Url);
            var hash = artifact.Sha1;
            if (artifact.Kind == "quilt" && hash is not null)
            {
                // [实现] 条件成立（artifact.Kind == "quilt" && hash is not null）时：准备 var mavenHash = (await http.GetStringAsync(uri.AbsoluteUri + ".sha1", ct)).Trim() .Split((char[…；执行 RequireConsistentChecksums(hash, mavenHash)。
                var mavenHash = (await http.GetStringAsync(uri.AbsoluteUri + ".sha1", ct)).Trim()
                    .Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                RequireConsistentChecksums(hash, mavenHash);
            }

            if (hash is null)
                hash = (await http.GetStringAsync(uri.AbsoluteUri + ".sha1", ct)).Trim()
                    .Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
            if (!Regex.IsMatch(hash ?? "", "^[0-9a-fA-F]{40}$"))
                throw new InvalidDataException("官方校验值无效。");
            spec = new(
                uri,
                destination,
                artifact.Size,
                hash!.ToLowerInvariant()
            );
        }

        SavedArtifact saved;
        try
        {
            // [实现] 在异常保护范围内：执行 saved = await SaveVerifiedAsync(service.Downloads, spec, verification, ct)。
            saved = await SaveVerifiedAsync(service.Downloads, spec, verification, ct);
        }
        catch (InvalidDataException ex)when (artifact.Kind == "quilt")
        {
            // [实现] 捕获 (InvalidDataException ex) 后：抛出 new InvalidDataException("Quilt 官方目录校验值与下载文件不符，未保存。请等待上游修复，或在官网手动核验文件。", ex)。
            throw new InvalidDataException("Quilt 官方目录校验值与下载文件不符，未保存。请等待上游修复，或在官网手动核验文件。", ex);
        }

        JsonFiles.Write(
            Path.Combine(
                service.Paths.Evidence,
                "artifact-" + Guid.NewGuid()
                    .ToString("N") + ".json"
            ),
            new
            {
                gameVersion = service.GameVersion,
                artifact.Kind,
                artifact.Version,
                saved,
                executed = false,
                savedAt = DateTimeOffset.UtcNow
            }
        );
        return saved;
    }

    // [实现] RequireConsistentChecksums 的实现：按 !Regex.IsMatch(metadataHash, "^[a-fA-F0-9]{40}$") || !Regex.IsMatch(mavenHash, "^[a-fA-F0-9]{40… 分支处理。
    public static void RequireConsistentChecksums(string metadataHash, string mavenHash)
    {
        if (!Regex.IsMatch(metadataHash, "^[a-fA-F0-9]{40}$") || !Regex.IsMatch(mavenHash, "^[a-fA-F0-9]{40}$") || !metadataHash.Equals(mavenHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Quilt 官方目录与官方 Maven 校验值不一致，未下载。请等待上游修复，或在官网手动核验文件。");
    }

    // [实现] SaveVerifiedAsync 的实现：准备 var path = Path.GetFullPath(spec.Path)；执行 OfficialUri(spec.Url.AbsoluteUri)；按 File.Exists(path) || Directory.Exists(path) 分支处理。
    public static async Task<SavedArtifact> SaveVerifiedAsync(
        DownloadService downloads,
        DownloadItem spec,
        string verification,
        CancellationToken ct = default
    )
    {
        var path = Path.GetFullPath(spec.Path);
        OfficialUri(spec.Url.AbsoluteUri);
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("目标已经存在，请使用其他文件名。");
        var pending = path + ".partial-" + Guid.NewGuid()
            .ToString("N");
        try
        {
            // [实现] 在异常保护范围内：执行 await downloads.DownloadAsync( spec with { Path = pending }, ct )；在作用域内使用并释放 var archive = ZipFile.OpenRead(pending)；准备 var content = File.OpenRead(pending)。
            await downloads.DownloadAsync(
                spec with
                {
                    Path = pending
                },
                ct
            );
            using (var archive = ZipFile.OpenRead(pending))
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("下载文件不是有效的 JAR。");
            using var content = File.OpenRead(pending);
            var sha256 = Convert.ToHexString(
                SHA256.HashData(content)
            )
                .ToLowerInvariant();
            var size = content.Length;
            content.Dispose();
            ct.ThrowIfCancellationRequested();
            File.Move(pending, path, false);
            return new(path, spec.Url.AbsoluteUri, size, sha256, spec.Sha1, verification);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(pending) 分支处理；按 File.Exists(pending + ".part") 分支处理。
            if (File.Exists(pending))
                File.Delete(pending);
            if (File.Exists(pending + ".part"))
                File.Delete(pending + ".part");
        }
    }

    // [实现] NotesAsync 的实现：准备 string root = "https://launchercontent.mojang.com/v2/"；准备 var index = JObject.Parse( await http.GetStringAsync(root + "javaPatchNotes.json", ct) )；准备 var entry = index["entries"]!.OfType<JObject>() .FirstOrDefault( x => (string? )x["version"] ==…。
    public async Task<VersionNotes?> NotesAsync(CancellationToken ct = default)
    {
        const string root = "https://launchercontent.mojang.com/v2/";
        var index = JObject.Parse(
            await http.GetStringAsync(root + "javaPatchNotes.json", ct)
        );
        var entry = index["entries"]!.OfType<JObject>()
            .FirstOrDefault(
                x => (string? )x["version"] == service.GameVersion
            );
        if (entry is null)
            return null;
        var source = new Uri(
            new Uri(root),
            (string)entry["contentPath"]!
        );
        if (source.Scheme != "https" || source.Host != "launchercontent.mojang.com" || !source.AbsolutePath.StartsWith("/v2/javaPatchNotes/", StringComparison.Ordinal))
            throw new InvalidDataException("官方更新日志地址无效。");
        var note = JObject.Parse(
            await http.GetStringAsync(source, ct)
        );
        return new(
            (string? )note["title"] ?? service.GameVersion,
            PlainText((string? )note["body"] ?? ""),
            source
        );
    }

    // [实现] PlainText 的实现：执行 html = Regex.Replace( html, "<(script|style)\\b[^>]*>.*?</\\1>", "", RegexOptions.IgnoreCase | …；执行 html = Regex.Replace( html, "</(?:p|li|h[1-6]|div)>|<br\\s* /?>", "\n", RegexOptions.IgnoreCase…；返回 WebUtility.HtmlDecode( Regex.Replace( html, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeco…。
    public static string PlainText(string html)
    {
        html = Regex.Replace(
            html,
            "<(script|style)\\b[^>]*>.*?</\\1>",
            "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1)
        );
        html = Regex.Replace(
            html,
            "</(?:p|li|h[1-6]|div)>|<br\\s*/?>",
            "\n",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)
        );
        return WebUtility.HtmlDecode(
            Regex.Replace(
                html,
                "<[^>]+>",
                "",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            )
        )
            .Trim();
    }

    // [实现] OfficialUri 的实现：准备 var uri = new Uri(url)；准备 string[] hosts = [ "piston-data.mojang.com", "launcher.mojang.com", "s3.amazonaws.com", "maven.…；按 uri.Scheme != "https" || !hosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) 分支处理。
    private static Uri OfficialUri(string url)
    {
        var uri = new Uri(url);
        string[] hosts = [
            "piston-data.mojang.com",
            "launcher.mojang.com",
            "s3.amazonaws.com",
            "maven.minecraftforge.net",
            "maven.neoforged.net",
            "maven.fabricmc.net",
            "maven.quiltmc.org",
            "optifine.net",
            "dl.liteloader.com",
            "repo.liteloader.com",
            "repo.mumfrey.com"
        ];
        if (uri.Scheme != "https" || !hosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("下载地址不属于已适配的官方来源。");
        return uri;
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
