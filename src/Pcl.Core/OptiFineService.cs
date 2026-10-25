using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：OptiFineRelease 汇集 FileName、GameVersion、Preview，供该模块传递状态。
public sealed record OptiFineRelease(string FileName, string GameVersion, bool Preview)
{
    // [实现] ToString 的实现：计算并返回 FileName.Replace("OptiFine_", "") .Replace(".jar", "")。
    public override string ToString() => FileName.Replace("OptiFine_", "")
        .Replace(".jar", "");
}

/// <summary>Downloads from the author's website for local use and invokes the included official patcher.</summary>
public sealed class OptiFineService(MinecraftService service) : IDisposable
{
    public const string CatalogUrl = "https://optifine.net/downloads";
    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    // [实现] VersionsAsync 的实现：准备 var html = await http.GetStringAsync(CatalogUrl, ct)；返回 ParseVersions(html, service.GameVersion)。
    public async Task<IReadOnlyList<OptiFineRelease>> VersionsAsync(CancellationToken ct = default)
    {
        var html = await http.GetStringAsync(CatalogUrl, ct);
        return ParseVersions(html, service.GameVersion);
    }

    // [实现] ParseVersions 的实现：计算并返回 Regex.Matches( WebUtility.HtmlDecode(html), @"(?:preview_)?OptiFine_(?<game>[0-9.]+)_HD_U_[A-Za…。
    public static IReadOnlyList<OptiFineRelease> ParseVersions(string html, string gameVersion) => Regex.Matches(
        WebUtility.HtmlDecode(html),
        @"(?:preview_)?OptiFine_(?<game>[0-9.]+)_HD_U_[A-Za-z0-9_]+\.jar"
    )
        .Select(
            x => new OptiFineRelease(
                x.Value,
                x.Groups["game"].Value,
                x.Value.StartsWith("preview_") || x.Value.Contains("_pre")
            )
        )
        .Where(
            x => x.GameVersion == gameVersion
        )
        .DistinctBy(
            x => x.FileName
        )
        .ToArray();
    // [实现] DownloadSpecAsync 的实现：按 !(await VersionsAsync(ct)).Any( x => x.FileName == fileName ) 分支处理；准备 var page = await http.GetStringAsync( "https://optifine.net/adloadx?f=" + Uri.EscapeDataString(…；准备 var match = Regex.Match( WebUtility.HtmlDecode(page), "href=['\"](?<path>downloadx[?][^'\"]+)['…。
    public async Task<DownloadItem> DownloadSpecAsync(string fileName, string destination, CancellationToken ct = default)
    {
        if (!(await VersionsAsync(ct)).Any(
            x => x.FileName == fileName
        ))
            throw new InvalidDataException("OptiFine 官方目录中没有此文件。");
        var page = await http.GetStringAsync(
            "https://optifine.net/adloadx?f=" + Uri.EscapeDataString(fileName),
            ct
        );
        var match = Regex.Match(
            WebUtility.HtmlDecode(page),
            "href=['\"](?<path>downloadx[?][^'\"]+)['\"]"
        );
        if (!match.Success)
            throw new InvalidDataException("OptiFine 官网未提供下载链接，请通过官网手动下载；未使用第三方镜像。");
        var url = new Uri(
            new Uri("https://optifine.net/"),
            match.Groups["path"].Value
        );
        if (url.Host != "optifine.net" || url.Scheme != "https" || url.AbsolutePath != "/downloadx")
            throw new InvalidDataException("OptiFine 下载地址不属于官方网站。");
        using var hashesStream = typeof(OptiFineService).Assembly.GetManifestResourceStream("Pcl.Core.Compatibility.pcl-optifine-hashes.json")!;
        using var hashesReader = new StreamReader(hashesStream);
        var hashes = JObject.Parse(
            hashesReader.ReadToEnd()
        );
        var expected = hashes.Properties()
            .FirstOrDefault(
                x => (string? )x.Value == fileName
            )?.Name;
        return new(
            url,
            Path.GetFullPath(destination),
            -1,
            expected
        );
    }

    // [实现] InstallAsync 的实现：准备 var versions = await VersionsAsync(ct)；准备 var release = fileName is null ? versions.FirstOrDefault( x => !x.Preview ) : versions.FirstOrD…；按 release is null 分支处理。
    public async Task<GameProfile> InstallAsync(
        string? fileName = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? instanceName = null,
        LoaderRecoveryContext? recovery = null
    )
    {
        var versions = await VersionsAsync(ct);
        var release = fileName is null
            ? versions.FirstOrDefault(
                x => !x.Preview
            )
            : versions.FirstOrDefault(
                x => x.FileName == fileName
            );
        if (release is null)
            throw new NotSupportedException("OptiFine 官方目录中没有此游戏版本的对应版本。");
        var id = instanceName ?? "optifine-" + Path.GetFileNameWithoutExtension(release.FileName)
            .Replace("OptiFine_", "");
        ForgeService.ValidateName(id);
        var target = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, "instances"),
            id
        );
        if (Directory.Exists(target) && recovery is null)
        {
            // [实现] 条件成立（Directory.Exists(target)）时：准备 var profile = InstanceRegistry.ByFolder(service, id)；准备 var installed = InstanceRegistry.Load(service, profile)；按 profile.Loader != "optifine" || installed.Libraries.Any( x => !DownloadService.IsValid(x) ) 分支处理。
            var profile = InstanceRegistry.ByFolder(service, id);
            var installed = InstanceRegistry.Load(service, profile);
            if (profile.Loader != "optifine" || installed.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
                throw new InvalidDataException("现有 OptiFine 实例损坏或不匹配，请先备份并修复。");
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

        var java = await new JavaService(service).EnsureForAsync(parent, progress, ct);
        var archive = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, "cache/optifine"),
            release.FileName
        );
        var download = await DownloadSpecAsync(release.FileName, archive, ct);
        // 无上游哈希时，恢复不能盲信旧缓存；在新的受控缓存路径重新取回作者 HTTPS 原件再比较已固定 SHA-256。
        if (recovery is not null && download.Sha1 is null)
        {
            archive = Path.Combine(
                service.Paths.Root,
                "cache/optifine",
                "operation-" + Guid.NewGuid()
                    .ToString("N"),
                release.FileName
            );
            download = download with
            {
                Path = archive
            };
        }

        var expected = download.Sha1;
        progress?.Report(
            new("下载官方 OptiFine", 0, 0, 0, 0)
        );
        await service.Downloads.DownloadAsync(download, ct);
        var sourceHash = Convert.ToHexString(
            SHA256.HashData(
                File.ReadAllBytes(archive)
            )
        )
            .ToLowerInvariant();
        recovery?.Report(
            "installer-planned",
            sourceHash,
            LoaderRecoveryContext.Hash(
                new JObject
                {
                    ["file"] = release.FileName,
                    ["sourceSha256"] = sourceHash
                }
            )
        );
        var stage = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, ".work"),
            id + "-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(stage);
        try
        {
            // [实现] 在异常保护范围内：准备 var zip = ZipFile.OpenRead(archive)；准备 var wrapper = zip.Entries.SingleOrDefault( x => Regex.IsMatch(x.FullName, @"^launchwrapper-of-[…；准备 var wrapperVersion = wrapper.Name[17..^4]。
            using var zip = ZipFile.OpenRead(archive);
            var wrapper = zip.Entries.SingleOrDefault(
                x => Regex.IsMatch(x.FullName, @"^launchwrapper-of-[A-Za-z0-9.]+\.jar$")
            ) ?? throw new NotSupportedException("此官方 OptiFine 安装器不包含受支持的 LaunchWrapper；未猜测替代依赖。");
            var wrapperVersion = wrapper.Name[17..^4];
            var optifineVersion = release.FileName.Replace("preview_", "")
                .Replace("OptiFine_", "")[..^4];
            var wrapperPath = "optifine/launchwrapper-of/" + wrapperVersion + "/" + wrapper.Name;
            var patchedPath = "optifine/OptiFine/" + optifineVersion + "/OptiFine-" + optifineVersion + ".jar";
            var wrapperFile = ResourceInstaller.SafeTarget(stage, "libraries/" + wrapperPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(wrapperFile)!
            );
            wrapper.ExtractToFile(wrapperFile);
            var patched = ResourceInstaller.SafeTarget(stage, "libraries/" + patchedPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(patched)!
            );
            progress?.Report(
                new("运行官方 OptiFine 补丁器", 0, 0, 0, 0)
            );
            recovery?.Report("installer-running", sourceHash, "");
            var command = await service.Commands.RunAsync(
                new(
                    java.Executable,
                    [
                        "-cp",
                        archive,
                        "optifine.Patcher",
                        parent.JarPath,
                        archive,
                        patched
                    ],
                    stage
                ),
                ct: ct
            );
            var output = command.StandardOutput + "\n" + command.StandardError;
            File.WriteAllText(
                Path.Combine(service.Paths.Evidence, id + "-patcher.log"),
                output
            );
            if (command.ExitCode != 0 || !File.Exists(patched))
                throw new InvalidDataException("OptiFine 官方补丁器失败，详见日志。");
            using (var patchedZip = ZipFile.OpenRead(patched))
                if (patchedZip.GetEntry("optifine/OptiFineTweaker.class")is null)
                    throw new InvalidDataException("OptiFine 补丁产物缺少启动入口。");
            // [实现] Generated 的实现：准备 var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative)；返回 new JObject { ["name"] = name, ["downloads"] = new JObject { ["artifact"] = new JObject { ["pat…。
            JObject Generated(string name, string relative)
            {
                var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative);
                return new JObject
                {
                    ["name"] = name,
                    ["downloads"] = new JObject
                    {
                        ["artifact"] = new JObject
                        {
                            ["path"] = relative,
                            ["url"] = "",
                            ["size"] = new FileInfo(file).Length,
                            ["sha1"] = Convert.ToHexString(
                                SHA1.HashData(
                                    File.ReadAllBytes(file)
                                )
                            )
                                .ToLowerInvariant(),
                            ["pclInstallerGenerated"] = true
                        }
                    }
                };
            }

            foreach (var item in parent.Libraries)
            {
                // [实现] 对 parent.Libraries 中的每一项：准备 var relative = Path.GetRelativePath( Path.Combine(service.Paths.Game, "libraries"), item.Path )；准备 var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative)；执行 Directory.CreateDirectory( Path.GetDirectoryName(file)! )。
                var relative = Path.GetRelativePath(
                    Path.Combine(service.Paths.Game, "libraries"),
                    item.Path
                );
                var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(file)!
                );
                File.Copy(item.Path, file, true);
            }

            var child = new JObject
            {
                ["id"] = id,
                ["mainClass"] = "net.minecraft.launchwrapper.Launch",
                ["arguments"] = new JObject
                {
                    ["game"] = new JArray("--tweakClass", "optifine.OptiFineTweaker")
                },
                ["libraries"] = new JArray(
                    Generated("optifine:OptiFine:" + optifineVersion, patchedPath),
                    Generated("optifine:launchwrapper-of:" + wrapperVersion, wrapperPath)
                )
            };
            var merged = FabricService.Merge(
                JsonFiles.Read(parent.JsonPath),
                child
            );
            recovery?.Report(
                "loader-verified",
                sourceHash,
                "",
                MinecraftService.BuildLibraries(merged, stage, service.Platform)
            );
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
                    loader = "optifine",
                    loaderVersion = optifineVersion,
                    displayName = service.GameVersion + " · OptiFine " + optifineVersion[(service.GameVersion.Length + 1)..],
                    libraryRoot = "instance",
                    source = CatalogUrl,
                    sourceSha256 = sourceHash,
                    expectedSha1 = expected,
                    expectedHashOrigin = expected is null
                        ? "none_https_author_download_local_receipt"
                        : "PCL2_2.99.0.0_OptifineHashes",
                    patcherExit = command.ExitCode,
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
                service.GameVersion + " · OptiFine " + optifineVersion,
                service.GameVersion,
                "optifine",
                target,
                Path.Combine(target, "version.json")
            );
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(stage) 分支处理；重新抛出当前异常。
            if (Directory.Exists(stage))
                Directory.Move(stage, stage + ".failed");
            throw;
        }
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
