using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：MacPinnedResource 汇集 ProjectId、VersionId、Sha1、Size、Kind、Name，供该模块传递状态。
public sealed record MacPinnedResource(string ProjectId, string VersionId, string Sha1, long Size, ResourceKind Kind, string Name);
// [实现] 数据约定：MacSetupResult 汇集 Profile、Java、ExperimentalShaders、Resources、ShaderReport、ShaderPreset，供该模块传递状态。
public sealed record MacSetupResult(
    GameProfile Profile,
    JavaRuntime Java,
    bool ExperimentalShaders,
    IReadOnlyList<ManagedResource> Resources,
    ShaderMacReport? ShaderReport,
    string? ShaderPreset
);
// Explicitly reviewed for 1.21.1. These are reproducible combinations, not a claim of upstream Apple driver support.
public sealed class MacSetupService(MinecraftService service)
{
    public const string Loader = "0.19.5";
    public static readonly MacPinnedResource FabricApi = new(
        "P7dR8mSH",
        "Mys3P7lK",
        "810b2b0195371a012906241d8b85a32a1d6de53c",
        2452735,
        ResourceKind.Mod,
        "Fabric API"
    );
    public static readonly MacPinnedResource Iris = new(
        "YL57xq9U",
        "zsoi0dso",
        "4e0df4011503b4b36ba69877ea7247c1a47fea00",
        2690177,
        ResourceKind.Mod,
        "Iris"
    );
    public static readonly MacPinnedResource Sodium = new(
        "AANobbMI",
        "u1OEbNKx",
        "928a2598178c3a58b0638bab842f467d2e49251a",
        1297761,
        ResourceKind.Mod,
        "Sodium"
    );
    public static readonly MacPinnedResource Complementary = new(
        "HVnmMxH1",
        "Bqen1mJX",
        "838139b54cddb56b2e83cd260d8efd960ac536d6",
        553397,
        ResourceKind.Shader,
        "Complementary Reimagined"
    );
    // [实现] CheckPinned 的实现：按 release.ProjectId != pinned.ProjectId || release.Id != pinned.VersionId || release.File.Sha1 !=… 分支处理。
    public static void CheckPinned(MacPinnedResource pinned, ResourceRelease release)
    {
        if (release.ProjectId != pinned.ProjectId || release.Id != pinned.VersionId || release.File.Sha1 != pinned.Sha1 || release.File.Size != pinned.Size || !release.GameVersions.Contains(MinecraftService.Version))
            throw new InvalidDataException("资源元数据已变更，需重新评估 Mac 配置：" + pinned.Name);
    }

    // [实现] InstallAsync 的实现：按 service.GameVersion != MinecraftService.Version 分支处理；按 service.Platform.Os != "osx" || service.Platform.Architecture != "aarch64" 分支处理；准备 var java = await new JavaService(service).EnsureJava21Async(progress, ct)。
    public async Task<MacSetupResult> InstallAsync(bool shaders, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (service.GameVersion != MinecraftService.Version)
            throw new NotSupportedException("此 Mac 光影预设固定为 Minecraft 1.21.1；其他版本请从资源页选择兼容模组。 ");
        if (service.Platform.Os != "osx" || service.Platform.Architecture != "aarch64")
            throw new PlatformNotSupportedException("此配置针对 Apple Silicon Mac。");
        var java = await new JavaService(service).EnsureJava21Async(progress, ct);
        try
        {
            // [实现] 在异常保护范围内：执行 service.LoadInstalled()。
            service.LoadInstalled();
        }
        catch (Exception ex)when (ex is FileNotFoundException or InvalidDataException)
        {
            // [实现] 捕获 (Exception ex) 后：执行 await service.InstallAsync(progress, ct)。
            await service.InstallAsync(progress, ct);
        }

        using var fabric = new FabricService(
            service,
            shaders
                ? "mac-shaders-1.21.1"
                : "mac-base-1.21.1"
        );
        if (File.Exists(fabric.ProfileFile) && (string? )JsonFiles.Read(fabric.ProfileFile)["loaderVersion"] != Loader)
            throw new InvalidOperationException("此 Mac 实例的加载器已被手动修改，未覆盖。请先备份并在版本管理中处理。");
        var profile = await fabric.InstallAsync(Loader, progress, ct);
        var nativeAudit = MacNativeAudit.Check(
            fabric.LoadInstalled(profile),
            java
        );
        JsonFiles.Write(
            Path.Combine(service.Paths.Evidence, "mac-native-audit.json"),
            nativeAudit
        );
        using var catalog = new ResourceCatalog(paths: service.Paths);
        var store = new ResourceInstaller(profile.GameDirectory, service.Downloads);
        var pinned = shaders
            ? new[]
            {
                FabricApi,
                Iris,
                Sodium,
                Complementary
            }

            : new[]
            {
                FabricApi
            };
        var releases = new Dictionary<string, ResourceRelease>();
        foreach (var item in pinned)
        {
            // [实现] 对 pinned 中的每一项：准备 var release = await catalog.VersionAsync(item.VersionId, ct)；执行 CheckPinned(item, release)；执行 releases[item.ProjectId] = release with { Name = item.Name }。
            var release = await catalog.VersionAsync(item.VersionId, ct);
            CheckPinned(item, release);
            releases[item.ProjectId] = release with
            {
                Name = item.Name
            };
        }

        var mods = new List<ResourceRelease>();
        foreach (var item in pinned.Where(
            x => x.Kind == ResourceKind.Mod
        ))
        {
            // [实现] 对 pinned.Where( x => x.Kind == ResourceKind.Mod ) 中的每一项：准备 var plan = await catalog.ResolveAsync( releases[item.ProjectId], ResourceKind.Mod, MinecraftSer…；遍历 plan.Releases 处理 release。
            var plan = await catalog.ResolveAsync(
                releases[item.ProjectId],
                ResourceKind.Mod,
                MinecraftService.Version,
                ct: ct,
                preferred: releases
            );
            foreach (var release in plan.Releases)
                if (!mods.Any(
                    x => x.ProjectId == release.ProjectId
                ))
                    mods.Add(release);
        }

        // Inspect every candidate, including root-only packages, before committing the curated mod set.
        var modPlan = new ResourceInstallPlan(ResourceKind.Mod, FabricApi.ProjectId, mods);
        ResourceInstaller.ValidateConflicts(
            store.List(),
            modPlan
        );
        var cache = Path.Combine(profile.GameDirectory, ".pcl-downloads");
        foreach (var release in mods)
        {
            // [实现] 对 mods 中的每一项：执行 await service.Downloads.DownloadAsync( new( release.File.Url, ResourceInstaller.SafeTarget(cach…；执行 FabricModReader.Read( ResourceInstaller.SafeTarget(cache, release.File.Sha1) )。
            await service.Downloads.DownloadAsync(
                new(
                    release.File.Url,
                    ResourceInstaller.SafeTarget(cache, release.File.Sha1),
                    release.File.Size,
                    release.File.Sha1
                ),
                ct
            );
            FabricModReader.Read(
                ResourceInstaller.SafeTarget(cache, release.File.Sha1)
            );
        }

        if (shaders)
        {
            // [实现] 条件成立（shaders）时：准备 var checkedPlan = await new FabricModPlanner(catalog, service.Downloads, cache, Loader).Resolve…；按 !checkedPlan.Releases.Any( x => x.Id == Sodium.VersionId ) 分支处理。
            var checkedPlan = await new FabricModPlanner(catalog, service.Downloads, cache, Loader).ResolveAsync(
                releases[Iris.ProjectId],
                MinecraftService.Version,
                store.List(),
                profile.GameDirectory,
                progress,
                ct
            );
            if (!checkedPlan.Releases.Any(
                x => x.Id == Sodium.VersionId
            ))
                throw new InvalidDataException("光影前置组合与已评估版本不同。");
        }

        if (shaders)
        {
            await new IrisEnvironmentService(profile.GameDirectory, "fabric", service.GameVersion).InstallAsync(modPlan, service.Downloads, progress, ct);
        }
        else
        {
            await store.InstallAsync(modPlan, progress, ct);
        }

        ShaderMacReport? report = null;
        if (shaders)
        {
            // [实现] 条件成立（shaders）时：准备 var shader = releases[Complementary.ProjectId]；执行 await store.InstallAsync( new( ResourceKind.Shader, shader.ProjectId, new[] { shader } ), progr…；准备 var file = ResourceInstaller.SafeTarget(profile.GameDirectory, "shaderpacks/" + shader.File.Nam…。
            var shader = releases[Complementary.ProjectId];
            await store.InstallAsync(
                new(
                    ResourceKind.Shader,
                    shader.ProjectId,
                    new[]
                    {
                        shader
                    }
                ),
                progress,
                ct
            );
            var file = ResourceInstaller.SafeTarget(profile.GameDirectory, "shaderpacks/" + shader.File.Name);
            report = MacShaderCompatibility.Inspect(file);
            MacShaderCompatibility.EnsureCanActivate(file);
            var options = MacShaderCompatibility.ProfileOptions(file, "MEDIUM")
                .ToDictionary();
            // Values are the publisher's preset; its native macOS branch disables custom-image colored lighting.
            if (options.GetValueOrDefault("COLORED_LIGHTING") != "0" || options.GetValueOrDefault("WORLD_SPACE_REFLECTIONS") != "-1")
                throw new InvalidDataException("中档预设已改变，需重新评估。");
            var config = file + ".txt";
            Backup(profile.GameDirectory, config);
            PropertySettings.Merge(config, options);
            store.Activate(shader.ProjectId);
        }

        PropertySettings.Merge(
            ResourceInstaller.SafeTarget(profile.GameDirectory, "options.txt"),
            new Dictionary<string, string>
            {
                ["renderDistance"] = "8",
                ["simulationDistance"] = "6",
                ["maxFps"] = "60"
            },
            ':',
            onlyMissing: true
        );
        var result = new MacSetupResult(
            profile,
            java,
            shaders,
            store.List(),
            report,
            shaders
                ? "Complementary MEDIUM"
                : null
        );
        JsonFiles.Write(
            Path.Combine(profile.GameDirectory, "pcl-mac-setup.json"),
            result
        );
        JsonFiles.Write(
            Path.Combine(
                service.Paths.Evidence,
                shaders
                    ? "mac-shaders-setup.json"
                    : "mac-base-setup.json"
            ),
            result
        );
        return result;
    }

    // [实现] Backup 的实现：按 !File.Exists(file) 分支处理；准备 var destination = ResourceInstaller.SafeTarget( root, ".pcl-backups/mac-config-" + Guid.NewGuid…；执行 Directory.CreateDirectory( Path.GetDirectoryName(destination)! )。
    private static void Backup(string root, string file)
    {
        if (!File.Exists(file))
            return;
        var destination = ResourceInstaller.SafeTarget(
            root,
            ".pcl-backups/mac-config-" + Guid.NewGuid()
                .ToString("N") + "/" + Path.GetFileName(file)
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!
        );
        File.Copy(file, destination);
    }
}
