namespace Pcl.Core;
// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class VersionInstaller(MinecraftService service)
{
    // [实现] InstallAsync 的实现：按 loader == "liteloader" 分支处理；按 loader == "optifine" 分支处理；按 loader is not ("auto" or "vanilla" or "fabric" or "quilt" or "forge" or "neoforge") 分支处理。
    public async Task<GameProfile> InstallAsync(
        string loader = "auto",
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (loader == "liteloader")
        {
            // [实现] 条件成立（loader == "liteloader"）时：准备 var lite = new LiteLoaderService(service)；返回 await lite.InstallAsync(progress: progress, ct: ct)。
            using var lite = new LiteLoaderService(service);
            return await lite.InstallAsync(progress: progress, ct: ct);
        }

        if (loader == "optifine")
        {
            // [实现] 条件成立（loader == "optifine"）时：准备 var optifine = new OptiFineService(service)；返回 await optifine.InstallAsync(progress: progress, ct: ct)。
            using var optifine = new OptiFineService(service);
            return await optifine.InstallAsync(progress: progress, ct: ct);
        }

        if (loader is not ("auto" or "vanilla" or "fabric" or "quilt" or "forge" or "neoforge"))
            throw new ArgumentException("不支持的加载器。");
        using var fabric = new FabricService(
            service,
            loader: loader == "quilt"
                ? "quilt"
                : "fabric"
        );
        using var forge = new ForgeService(service);
        string? selectedVersion = null;
        if (loader is "auto" or "fabric" or "quilt")
        {
            // [实现] 条件成立（loader is "auto" or "fabric" or "quilt"）时：执行 selectedVersion = (await fabric.VersionsAsync(ct)).FirstOrDefault( x => x.Stable )?.Version；按 selectedVersion is null && loader is "fabric" or "quilt" 分支处理；执行 loader = selectedVersion is null ? "vanilla" : loader == "quilt" ? "quilt" : "fabric"。
            selectedVersion = (await fabric.VersionsAsync(ct)).FirstOrDefault(
                x => x.Stable
            )?.Version;
            if (selectedVersion is null && loader is "fabric" or "quilt")
                throw new NotSupportedException("此游戏版本没有稳定的 " + loader + " 加载器。");
            loader = selectedVersion is null
                ? "vanilla"
                : loader == "quilt"
                    ? "quilt"
                    : "fabric";
        }
        else if (loader is "forge" or "neoforge")
            selectedVersion = (await forge.VersionsAsync(loader, ct)).FirstOrDefault()?.Version ?? throw new NotSupportedException("此游戏版本没有可用的 " + loader + " 正式版本。");
        var version = await service.InstallAsync(progress, ct);
        var java = await new JavaService(service).EnsureForAsync(version, progress, ct);
        if (loader == "vanilla")
        {
            // [实现] 条件成立（loader == "vanilla"）时：准备 var settings = new LauncherSettingsStore(service.Paths)；执行 settings.SavePreferences( service.VanillaGameDirectory, settings.Preferences(service.VanillaGam…。
            var settings = new LauncherSettingsStore(service.Paths);
            settings.SavePreferences(
                service.VanillaGameDirectory,
                settings.Preferences(service.VanillaGameDirectory)with
                {
                    Deleted = false,
                    Hidden = false
                }
            );
        }

        var profile = loader == "vanilla"
            ? InstanceRegistry.List(service, true)
                .Single(
                    x => x.Loader == "vanilla" && x.GameVersion == service.GameVersion && Path.GetFullPath(x.GameDirectory) == Path.GetFullPath(service.VanillaGameDirectory)
                )
            : loader is "fabric" or "quilt"
                ? await fabric.InstallAsync(selectedVersion, progress, ct)
                : await forge.InstallAsync(loader, selectedVersion, progress: progress, ct: ct);
        JsonFiles.Write(
            Path.Combine(service.Paths.Evidence, service.GameVersion + "-version-install.json"),
            new
            {
                status = "passed",
                profile,
                java,
                selectedLoader = loader,
                selectedVersion,
                timestamp = DateTimeOffset.UtcNow
            }
        );
        return profile;
    }
}
