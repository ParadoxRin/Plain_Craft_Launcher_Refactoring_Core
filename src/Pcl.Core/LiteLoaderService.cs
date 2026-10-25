using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：LiteLoaderRelease 汇集 Version、Build、TweakClass、Repository、Metadata、Libraries，供该模块传递状态。
public sealed record LiteLoaderRelease(
    string Version,
    string Build,
    string TweakClass,
    string Repository,
    JObject Metadata,
    JArray Libraries
)
{
    // [实现] ToString 的实现：计算并返回 Build。
    public override string ToString() => Build;
}

/// <summary>Uses the original LiteLoader catalog and Maven repository, including timestamped snapshots.</summary>
public sealed class LiteLoaderService(MinecraftService service) : IDisposable
{
    public const string CatalogUrl = "https://dl.liteloader.com/versions/versions.json";
    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    // [实现] VersionsAsync 的实现：准备 var catalog = JObject.Parse( await http.GetStringAsync(CatalogUrl, ct) )；按 catalog["versions"]?[service.GameVersion] is not JObject game 分支处理；准备 var repository = Https((string)game["repo"]!["url"]!)。
    public async Task<IReadOnlyList<LiteLoaderRelease>> VersionsAsync(CancellationToken ct = default)
    {
        var catalog = JObject.Parse(
            await http.GetStringAsync(CatalogUrl, ct)
        );
        if (catalog["versions"]?[service.GameVersion] is not JObject game)
            return Array.Empty<LiteLoaderRelease>();
        var repository = Https((string)game["repo"]!["url"]!);
        var releases = new List<LiteLoaderRelease>();
        foreach (var channel in new[]
        {
            "artefacts",
            "snapshots"
        }

        )
        {
            // [实现] 对 new[] { "artefacts", "snapshots" } 中的每一项：按 game[channel] is not JObject source || source["com.mumfrey:liteloader"] is not JObject entries 分支处理；遍历 entries.Properties() .Where( x => x.Name != "latest" ) 处理 entry。
            if (game[channel] is not JObject source || source["com.mumfrey:liteloader"] is not JObject entries)
                continue;
            foreach (var entry in entries.Properties()
                .Where(
                    x => x.Name != "latest"
                ))
            {
                // [实现] 对 entries.Properties() .Where( x => x.Name != "latest" ) 中的每一项：准备 var item = (JObject)entry.Value；准备 var libs = new JArray( (source["libraries"] ?? new JArray()).Concat( item["libraries"] ?? new J…；执行 releases.Add( new( (string)item["version"]!, (string? )item["build"] ?? (string)item["version"]…。
                var item = (JObject)entry.Value;
                var libs = new JArray(
                    (source["libraries"] ?? new JArray()).Concat(
                        item["libraries"] ?? new JArray()
                    )
                        .Select(
                            x => x.DeepClone()
                        )
                );
                releases.Add(
                    new(
                        (string)item["version"]!,
                        (string? )item["build"] ?? (string)item["version"]!,
                        (string)item["tweakClass"]!,
                        repository,
                        item,
                        libs
                    )
                );
            }
        }

        return releases.OrderByDescending(
            x => (long? )x.Metadata["timestamp"] ?? 0
        )
            .ToArray();
    }

    // [实现] DownloadSpecAsync 的实现：准备 var release = (await VersionsAsync(ct)).FirstOrDefault( x => x.Build == build || x.Version == b…；准备 var library = await ResolveMavenAsync("com.mumfrey:liteloader:" + release.Version, release.Repo…；准备 var artifact = library["downloads"]!["artifact"]!。
    public async Task<DownloadItem> DownloadSpecAsync(string build, string destination, CancellationToken ct = default)
    {
        var release = (await VersionsAsync(ct)).FirstOrDefault(
            x => x.Build == build || x.Version == build
        ) ?? throw new InvalidDataException("LiteLoader 官方目录中没有此版本。");
        var library = await ResolveMavenAsync("com.mumfrey:liteloader:" + release.Version, release.Repository, ct);
        var artifact = library["downloads"]!["artifact"]!;
        return new(
            new Uri((string)artifact["url"]!),
            Path.GetFullPath(destination),
            -1,
            (string)artifact["sha1"]!
        );
    }

    // [实现] InstallAsync 的实现：准备 var releases = await VersionsAsync(ct)；准备 var release = build is null ? releases.FirstOrDefault() : releases.FirstOrDefault( x => x.Build…；按 release is null 分支处理。
    public async Task<GameProfile> InstallAsync(
        string? build = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? instanceName = null,
        LoaderRecoveryContext? recovery = null
    )
    {
        var releases = await VersionsAsync(ct);
        var release = build is null
            ? releases.FirstOrDefault()
            : releases.FirstOrDefault(
                x => x.Build == build || x.Version == build
            );
        if (release is null)
            throw new NotSupportedException("官方 LiteLoader 目录没有此游戏版本的安装包。");
        var id = instanceName ?? "liteloader-" + service.GameVersion;
        ForgeService.ValidateName(id);
        var target = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, "instances"),
            id
        );
        if (Directory.Exists(target) && recovery is null)
        {
            // [实现] 条件成立（Directory.Exists(target)）时：准备 var profile = InstanceRegistry.ByFolder(service, id)；准备 var installed = InstanceRegistry.Load(service, profile)；按 profile.Loader != "liteloader" || installed.Libraries.Any( x => !DownloadService.IsValid(x) ) 分支处理。
            var profile = InstanceRegistry.ByFolder(service, id);
            var installed = InstanceRegistry.Load(service, profile);
            if (profile.Loader != "liteloader" || installed.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
                throw new InvalidDataException("既有 LiteLoader 实例不匹配或需要修复。");
            return profile;
        }

        InstalledVersion parent;
        try
        {
            // [实现] 在异常保护范围内：执行 parent = service.LoadInstalled()。
            parent = service.LoadInstalled();
        }
        catch (FileNotFoundException)
        {
            // [实现] 捕获 (FileNotFoundException) 后：执行 parent = await service.InstallAsync(progress, ct)。
            parent = await service.InstallAsync(progress, ct);
        }

        await new JavaService(service).EnsureForAsync(parent, progress, ct);
        var libraries = new List<JObject>();
        foreach (var source in release.Libraries.OfType<JObject>()
            .Append(
                new JObject
                {
                    ["name"] = "com.mumfrey:liteloader:" + release.Version,
                    ["url"] = release.Repository
                }
            )
            .DistinctBy(
                x => (string? )x["name"]
            ))
            libraries.Add(
                await ResolveMavenAsync(
                    (string)source["name"]!,
                    (string? )source["url"] ?? "https://libraries.minecraft.net/",
                    ct
                )
            );
        var child = new JObject
        {
            ["id"] = id,
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["arguments"] = new JObject
            {
                ["game"] = new JArray("--tweakClass", release.TweakClass)
            },
            ["libraries"] = new JArray(libraries)
        };
        var merged = FabricService.Merge(
            JsonFiles.Read(parent.JsonPath),
            child
        );
        recovery?.Report(
            "loader-planned",
            "",
            LoaderRecoveryContext.Hash(merged),
            MinecraftService.BuildLibraries(merged, service.Paths.Game, service.Platform)
        );
        await service.Downloads.DownloadManyAsync(
            MinecraftService.BuildLibraries(merged, service.Paths.Game, service.Platform),
            progress,
            ct
        );
        var stage = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, ".work"),
            id + "-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(stage);
        JsonFiles.Write(
            Path.Combine(stage, "version.json"),
            merged
        );
        JsonFiles.Write(
            Path.Combine(stage, "profile.json"),
            new
            {
                id,
                gameVersion = service.GameVersion,
                loader = "liteloader",
                loaderVersion = release.Build,
                displayName = service.GameVersion + " · LiteLoader",
                source = CatalogUrl,
                installedAt = DateTimeOffset.UtcNow
            }
        );
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!
        );
        recovery?.Report("loader-committing", "", "");
        if (recovery is null)
        {
            Directory.Move(stage, target);
        }
        else
        {
            recovery.Publish(stage, target, ct);
        }

        recovery?.Report("loader-committed", "", "");
        return new(
            id,
            service.GameVersion + " · LiteLoader",
            service.GameVersion,
            "liteloader",
            target,
            Path.Combine(target, "version.json")
        );
    }

    // [实现] ResolveMavenAsync 的实现：准备 var path = FabricService.MavenPath(coordinate)；准备 var parts = coordinate.Split(':')；按 coordinate.StartsWith("org.ow2.asm:", StringComparison.Ordinal) && repository == "https://libra… 分支处理。
    private async Task<JObject> ResolveMavenAsync(string coordinate, string repository, CancellationToken ct)
    {
        var path = FabricService.MavenPath(coordinate);
        var parts = coordinate.Split(':');
        if (coordinate.StartsWith("org.ow2.asm:", StringComparison.Ordinal) && repository == "https://libraries.minecraft.net/")
            repository = "https://repo.maven.apache.org/maven2/";
        if (repository.TrimEnd('/') == "https://repo.spongepowered.org/maven")
            repository = "https://repo.spongepowered.org/repository/maven-public/";
        var url = Https(repository)
            .TrimEnd('/') + "/" + path;
        if (parts[2].EndsWith("-SNAPSHOT", StringComparison.Ordinal))
        {
            // [实现] 条件成立（parts[2].EndsWith("-SNAPSHOT", StringComparison.Ordinal)）时：准备 var folder = url[..(url.LastIndexOf('/') + 1)]；准备 var xml = XDocument.Parse( await http.GetStringAsync(folder + "maven-metadata.xml", ct) )；准备 var snapshot = xml.Descendants("snapshotVersion") .FirstOrDefault( x => (string? )x.Element("ex…。
            var folder = url[..(url.LastIndexOf('/') + 1)];
            var xml = XDocument.Parse(
                await http.GetStringAsync(folder + "maven-metadata.xml", ct)
            );
            var snapshot = xml.Descendants("snapshotVersion")
                .FirstOrDefault(
                    x => (string? )x.Element("extension") == "jar" && x.Element("classifier")is null
                );
            var version = (string? )snapshot?.Element("value");
            if (version is null)
            {
                // [实现] 条件成立（version is null）时：准备 var timestamp = xml.Descendants("snapshot") .First() .Element("timestamp")?.Value；准备 var number = xml.Descendants("snapshot") .First() .Element("buildNumber")?.Value；按 !Regex.IsMatch(timestamp ?? "", @"^\d{8}\.\d{6}$") || !Regex.IsMatch(number ?? "", "^[0-9]+$") 分支处理。
                var timestamp = xml.Descendants("snapshot")
                    .First()
                    .Element("timestamp")?.Value;
                var number = xml.Descendants("snapshot")
                    .First()
                    .Element("buildNumber")?.Value;
                if (!Regex.IsMatch(timestamp ?? "", @"^\d{8}\.\d{6}$") || !Regex.IsMatch(number ?? "", "^[0-9]+$"))
                    throw new InvalidDataException("LiteLoader Maven 快照元数据无效。");
                version = parts[2][..^8] + timestamp + "-" + number;
            }

            if (!Regex.IsMatch(version, "^[A-Za-z0-9._+-]+$"))
                throw new InvalidDataException("Maven 快照标识无效。");
            url = folder + parts[1] + "-" + version + ".jar";
        }

        var sha1 = (await http.GetStringAsync(url + ".sha1", ct)).Trim()
            .Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!Regex.IsMatch(sha1, "^[0-9A-Fa-f]{40}$"))
            throw new InvalidDataException("LiteLoader 依赖 SHA-1 无效。");
        return new JObject
        {
            ["name"] = coordinate,
            ["downloads"] = new JObject
            {
                ["artifact"] = new JObject
                {
                    ["path"] = path,
                    ["url"] = url,
                    ["size"] = -1,
                    ["sha1"] = sha1.ToLowerInvariant()
                }
            }
        };
    }

    // [实现] Https 的实现：准备 var uri = new Uri(input)；按 uri.Scheme is not ("http" or "https") 分支处理；返回 new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri。
    private static string Https(string input)
    {
        var uri = new Uri(input);
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException("加载器依赖必须是 HTTP(S) 地址。");
        return new UriBuilder(uri)
        {
            Scheme = "https",
            Port = -1
        }.Uri.AbsoluteUri;
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
