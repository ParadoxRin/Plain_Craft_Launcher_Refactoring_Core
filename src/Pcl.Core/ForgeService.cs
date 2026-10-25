using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：ForgeRelease 汇集 Loader、Version、MavenVersion、BaseUrl，供该模块传递状态。
public sealed record ForgeRelease(string Loader, string Version, string MavenVersion, string BaseUrl)
{
    // [实现] ToString 的实现：计算并返回 Version。
    public override string ToString() => Version;
}

// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class ForgeService(MinecraftService service) : IDisposable
{
    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    // [实现] MavenBase 的实现：计算并返回 loader switch { "forge" => "https://maven.minecraftforge.net/net/minecraftforge/forge/", "neofo…。
    public static string MavenBase(string loader) => loader switch
    {
        "forge" => "https://maven.minecraftforge.net/net/minecraftforge/forge/",
        "neoforge" => "https://maven.neoforged.net/releases/net/neoforged/neoforge/",
        _ => throw new ArgumentException("加载器无效。")
    };
    // [实现] VersionsAsync 的实现：准备 var legacyNeo = loader == "neoforge" && service.GameVersion == "1.20.1"；准备 var root = legacyNeo ? "https://maven.neoforged.net/releases/net/neoforged/forge/" : MavenBase(…；准备 var xml = XDocument.Parse( await http.GetStringAsync(root + "maven-metadata.xml", ct) )。
    public async Task<IReadOnlyList<ForgeRelease>> VersionsAsync(string loader, CancellationToken ct = default)
    {
        var legacyNeo = loader == "neoforge" && service.GameVersion == "1.20.1";
        var root = legacyNeo
            ? "https://maven.neoforged.net/releases/net/neoforged/forge/"
            : MavenBase(loader);
        var xml = XDocument.Parse(
            await http.GetStringAsync(root + "maven-metadata.xml", ct)
        );
        return xml.Descendants("version")
            .Select(
                x => x.Value
            )
            .Where(
                x => x.StartsWith(
                    loader == "forge" || legacyNeo
                        ? service.GameVersion + "-"
                        : VersionCatalog.NeoForgePrefix(service.GameVersion)
                ) && !x.Contains("beta", StringComparison.OrdinalIgnoreCase)
            )
            .Select(
                x => new ForgeRelease(
                    loader,
                    loader == "forge" || legacyNeo
                        ? x[(service.GameVersion.Length + 1)..]
                        : x,
                    x,
                    root
                )
            )
            .OrderByDescending(
                x => System.Version.TryParse(x.Version, out var parsed)
                    ? parsed
                    : new System.Version(0, 0)
            )
            .ToArray();
    }

    // [实现] InstallAsync 的实现：准备 var versions = await VersionsAsync(loader, ct)；准备 var release = version is null ? versions.FirstOrDefault() ?? throw new NotSupportedException("此…；执行 instanceName ??= $"{loader}-{service.GameVersion}-{release.Version}"。
    public async Task<GameProfile> InstallAsync(
        string loader,
        string? version = null,
        string? instanceName = null,
        string? displayName = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        LoaderRecoveryContext? recovery = null
    )
    {
        var versions = await VersionsAsync(loader, ct);
        var release = version is null
            ? versions.FirstOrDefault() ?? throw new NotSupportedException("此 Minecraft 版本没有可用的 " + loader + " 正式版本。")
            : versions.FirstOrDefault(
                x => x.Version == version || x.MavenVersion == version
            ) ?? throw new InvalidDataException("官方版本列表没有指定版本。");
        instanceName ??= $"{loader}-{service.GameVersion}-{release.Version}";
        ValidateName(instanceName);
        var target = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, "instances"),
            instanceName
        );
        if (Directory.Exists(target) && recovery is null)
        {
            // [实现] 条件成立（Directory.Exists(target)）时：准备 var receipt = JsonFiles.Read( Path.Combine(target, "profile.json") )；按 (string? )receipt["loader"] != loader || (string? )receipt["loaderVersion"] != release.Version 分支处理；准备 var existing = InstanceRegistry.ByFolder(service, instanceName)。
            var receipt = JsonFiles.Read(
                Path.Combine(target, "profile.json")
            );
            if ((string? )receipt["loader"] != loader || (string? )receipt["loaderVersion"] != release.Version)
                throw new IOException("实例目录已存在且与请求不一致。");
            var existing = InstanceRegistry.ByFolder(service, instanceName);
            var check = InstanceRegistry.Load(service, existing);
            if (check.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
                throw new InvalidDataException("实例库文件不完整，未覆盖现有实例。请备份后重新安装到新名称。");
            return existing;
        }

        InstalledVersion parent;
        try
        {
            // [实现] 在异常保护范围内：执行 parent = service.LoadInstalled()。
            parent = service.LoadInstalled();
        }
        catch (Exception ex)when (ex is FileNotFoundException or InvalidDataException)
        {
            // [实现] 捕获 (Exception ex) 后：执行 parent = await service.InstallAsync(progress, ct)。
            parent = await service.InstallAsync(progress, ct);
        }

        var java = await new JavaService(service).EnsureForAsync(parent, progress, ct);
        var cache = Path.Combine(service.Paths.Root, "cache/installers", loader);
        Directory.CreateDirectory(cache);
        var installerFamily = release.BaseUrl.EndsWith("/forge/", StringComparison.Ordinal)
            ? "forge"
            : "neoforge";
        var name = $"{installerFamily}-{release.MavenVersion}-installer.jar";
        var url = release.BaseUrl + release.MavenVersion + "/" + name;
        var hash = (await http.GetStringAsync(url + ".sha1", ct)).Trim()
            .Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("官方安装器校验值无效。");
        recovery?.Report("installer-identified", hash, "");
        var installer = Path.Combine(cache, name);
        progress?.Report(
            new("下载 " + loader + " 安装器", 0, 0, 0, 0)
        );
        await service.Downloads.DownloadAsync(
            new(
                new Uri(url),
                installer,
                -1,
                hash
            ),
            ct
        );
        var stage = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, ".work"),
            instanceName + "-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(stage);
        var log = Path.Combine(service.Paths.Evidence, instanceName + "-installer.log");
        try
        {
            // [实现] 在异常保护范围内：准备 var versionFolder = Path.Combine(stage, "versions", service.GameVersion)；执行 Directory.CreateDirectory(versionFolder)；执行 File.Copy( parent.JsonPath, Path.Combine(versionFolder, service.GameVersion + ".json") )。
            var versionFolder = Path.Combine(stage, "versions", service.GameVersion);
            Directory.CreateDirectory(versionFolder);
            File.Copy(
                parent.JsonPath,
                Path.Combine(versionFolder, service.GameVersion + ".json")
            );
            File.Copy(
                parent.JarPath,
                Path.Combine(versionFolder, service.GameVersion + ".jar")
            );
            JsonFiles.Write(
                Path.Combine(stage, "launcher_profiles.json"),
                new
                {
                    profiles = new { },
                    version = 3
                }
            );
            foreach (var file in parent.Libraries)
            {
                // [实现] 对 parent.Libraries 中的每一项：准备 var relative = Path.GetRelativePath( Path.Combine(service.Paths.Game, "libraries"), file.Path )；准备 var destination = ResourceInstaller.SafeTarget(stage, "libraries/" + relative)；执行 Directory.CreateDirectory( Path.GetDirectoryName(destination)! )。
                var relative = Path.GetRelativePath(
                    Path.Combine(service.Paths.Game, "libraries"),
                    file.Path
                );
                var destination = ResourceInstaller.SafeTarget(stage, "libraries/" + relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!
                );
                File.Copy(file.Path, destination, true);
            }

            using (var archive = ZipFile.OpenRead(installer))
            {
                // [实现] 本段流程：准备 var profileReader = new StreamReader( (archive.GetEntry("install_profile.json") ?? throw new In…；准备 var versionReader = new StreamReader( (archive.GetEntry("version.json") ?? throw new NotSupport…；准备 var installMetadata = JObject.Parse( await profileReader.ReadToEndAsync(ct) )。
                using var profileReader = new StreamReader(
                    (archive.GetEntry("install_profile.json") ?? throw new InvalidDataException("官方安装器缺少 install_profile.json。")).Open()
                );
                using var versionReader = new StreamReader(
                    (archive.GetEntry("version.json") ?? throw new NotSupportedException("此 Forge 安装器采用较早的 versionInfo 格式；当前支持提供 version.json 的官方客户端安装器，尚未验证此旧安装器。")).Open()
                );
                var installMetadata = JObject.Parse(
                    await profileReader.ReadToEndAsync(ct)
                );
                var launchMetadata = JObject.Parse(
                    await versionReader.ReadToEndAsync(ct)
                );
                recovery?.Report(
                    "installer-planned",
                    hash,
                    LoaderRecoveryContext.Hash(
                        new JObject
                        {
                            ["install"] = installMetadata,
                            ["launch"] = launchMetadata
                        }
                    )
                );
                var prefetch = new List<DownloadItem>();
                foreach (var library in installMetadata["libraries"]!.Concat(launchMetadata["libraries"]!))
                {
                    // [实现] 对 installMetadata["libraries"]!.Concat(launchMetadata["libraries"]!) 中的每一项：准备 var artifact = library["downloads"]?["artifact"]；准备 var remote = (string? )artifact?["url"]；准备 var digest = (string? )artifact?["sha1"]。
                    var artifact = library["downloads"]?["artifact"];
                    var remote = (string? )artifact?["url"];
                    var digest = (string? )artifact?["sha1"];
                    if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(digest))
                        continue;
                    var uri = new Uri(remote);
                    if (uri.Scheme != "https")
                        throw new InvalidDataException("安装依赖要求 HTTPS。");
                    prefetch.Add(
                        new(
                            uri,
                            ResourceInstaller.SafeTarget(stage, "libraries/" + (string)artifact!["path"]!),
                            (long? )artifact!["size"] ?? -1,
                            digest
                        )
                    );
                }

                await service.Downloads.DownloadManyAsync(prefetch, progress, ct);
            }

            recovery?.Report("installer-running", hash, "");
            progress?.Report(
                new("运行官方 " + loader + " 客户端安装器", 0, 0, 0, 0)
            );
            var exit = await RunInstallerAsync(
                java.Executable,
                installer,
                stage,
                installerFamily,
                log,
                progress,
                ct,
                service.Commands
            );
            if (exit != 0)
                throw new InvalidDataException($"{loader} 官方安装器退出 {exit}；日志：{log}");
            using var zip = ZipFile.OpenRead(installer);
            using var reader = new StreamReader(
                zip.GetEntry("version.json")!.Open()
            );
            var child = JObject.Parse(
                await reader.ReadToEndAsync(ct)
            );
            if ((string? )child["inheritsFrom"] != service.GameVersion)
                throw new InvalidDataException("加载器继承版本不匹配。");
            var generated = new List<object>();
            foreach (var lib in child["libraries"]!.OfType<JObject>())
                NormalizeLibrary(lib, stage, generated);
            child["id"] = instanceName;
            var merged = FabricService.Merge(
                JsonFiles.Read(parent.JsonPath),
                child
            );
            var files = MinecraftService.BuildLibraries(merged, stage, service.Platform);
            foreach (var file in files)
                if (!DownloadService.IsValid(file))
                    throw new InvalidDataException("安装后库校验失败：" + file.Path);
            JsonFiles.Write(
                Path.Combine(stage, "version.json"),
                merged
            );
            File.Copy(
                parent.JarPath,
                Path.Combine(stage, instanceName + ".jar")
            );
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                new
                {
                    id = instanceName,
                    gameVersion = service.GameVersion,
                    loader,
                    loaderVersion = release.Version,
                    displayName = displayName ?? $"{service.GameVersion} · {loader} {release.Version}",
                    libraryRoot = "instance",
                    clientJar = instanceName + ".jar",
                    installerUrl = url,
                    installerSha1 = hash,
                    officialInstallerExit = exit,
                    generatedArtifacts = generated,
                    installedAt = DateTimeOffset.UtcNow
                }
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(target)!
            );
            ct.ThrowIfCancellationRequested();
            recovery?.Report("loader-committing", hash, "", files);
            if (recovery is null)
            {
                Directory.Move(stage, target);
            }
            else
            {
                recovery.Publish(stage, target, ct);
            }

            recovery?.Report("loader-committed", hash, "");
            var profile = new GameProfile(
                instanceName,
                displayName ?? $"{service.GameVersion} · {loader} {release.Version}",
                service.GameVersion,
                loader,
                target,
                Path.Combine(target, "version.json")
            );
            JsonFiles.Write(
                Path.Combine(service.Paths.Evidence, instanceName + "-install-result.json"),
                new
                {
                    status = "passed",
                    profile,
                    installerUrl = url,
                    installerSha1 = hash,
                    generatedArtifacts = generated,
                    log
                }
            );
            return profile;
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(stage) 分支处理；重新抛出当前异常。
            if (Directory.Exists(stage))
                Directory.Move(stage, stage + ".failed");
            throw;
        }
    }

    // [实现] ValidateName 的实现：按 !Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$") || name.Contains("..") 分支处理。
    public static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$") || name.Contains(".."))
            throw new InvalidDataException("实例标识无效。");
    }

    // [实现] NormalizeLibrary 的实现：准备 var artifact = library["downloads"]?["artifact"] as JObject ?? throw new InvalidDataException("…；准备 var relative = (string)artifact["path"]!；准备 var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative)。
    private static void NormalizeLibrary(JObject library, string stage, List<object> generated)
    {
        var artifact = library["downloads"]?["artifact"] as JObject ?? throw new InvalidDataException("加载器库缺少 artifact 元数据。");
        var relative = (string)artifact["path"]!;
        var file = ResourceInstaller.SafeTarget(stage, "libraries/" + relative);
        if (!File.Exists(file))
            throw new FileNotFoundException("安装器未生成所需文件。", file);
        var sha1 = Convert.ToHexString(
            SHA1.HashData(
                File.ReadAllBytes(file)
            )
        )
            .ToLowerInvariant();
        var expected = (string? )artifact["sha1"];
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装器产物 SHA-1 与官方元数据不符：" + relative);
        if ((long? )artifact["size"] is> 0 && (long)artifact["size"]! != new FileInfo(file).Length)
            throw new InvalidDataException("安装器产物大小不符：" + relative);
        artifact["size"] = new FileInfo(file).Length;
        artifact["sha1"] = sha1;
        if (string.IsNullOrWhiteSpace((string? )artifact["url"]))
        {
            // [实现] 条件成立（string.IsNullOrWhiteSpace((string? )artifact["url"])）时：执行 artifact["pclInstallerGenerated"] = true；执行 generated.Add( new { path = relative, sha1, hashOrigin = string.IsNullOrEmpty(expected) ? "loca…。
            artifact["pclInstallerGenerated"] = true;
            generated.Add(
                new
                {
                    path = relative,
                    sha1,
                    hashOrigin = string.IsNullOrEmpty(expected)
                        ? "local_receipt_after_official_installer"
                        : "official_version_metadata"
                }
            );
        }
    }

    // 安装器属于短命令：通过注入 Runner 排空双流，实时写入安装日志并映射 Task/Patching 进度。
    public static async Task<int> RunInstallerAsync(
        string java,
        string installer,
        string stage,
        string loader,
        string log,
        IProgress<InstallProgress>? progress,
        CancellationToken ct,
        Pcl.Core.Ports.Processes.IShellCommandRunner? commands = null
    )
    {
        using var writer = new StreamWriter(log)
        {
            AutoFlush = true
        };
        var output = new InstallerProgress(
            line =>
            {
                writer.WriteLine(line.Text);
                if (line.Text.StartsWith("Task:") || line.Text.StartsWith("Patching"))
                {
                    progress?.Report(
                        new(line.Text, 0, 0, 0, 0)
                    );
                }
            }
        );
        var result = await (commands ?? new Pcl.Core.Adapters.Processes.SystemShellCommandRunner()).RunAsync(
            new(
                java,
                [
                    "-Djava.awt.headless=true",
                    "-jar",
                    installer,
                    loader == "forge"
                        ? "--installClient"
                        : "--install-client",
                    stage
                ],
                stage
            ),
            output,
            ct
        )
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    private sealed class InstallerProgress(Action<Pcl.Core.Ports.Processes.ProcessLogLine> report) : IProgress<Pcl.Core.Ports.Processes.ProcessLogLine>
    {
        public void Report(Pcl.Core.Ports.Processes.ProcessLogLine value) => report(value);
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
