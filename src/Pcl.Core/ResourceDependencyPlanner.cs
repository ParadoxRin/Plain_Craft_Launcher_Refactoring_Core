namespace Pcl.Core;
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class ResourceDependencyPlanner
{
    // [实现] ResolveAsync 的实现：按 kind != ResourceKind.Mod || profile.Loader is not ("fabric" or "quilt") 分支处理；准备 var loader = FabricService.LoaderFor(profile)；准备 var installed = new ResourceInstaller(profile.GameDirectory, downloads, profile.Loader, profile…。
    public static async Task<ResourceInstallPlan> ResolveAsync(
        ResourceCatalog catalog,
        DownloadService downloads,
        ResourceRelease release,
        ResourceKind kind,
        GameProfile profile,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (kind != ResourceKind.Mod || profile.Loader is not ("fabric" or "quilt"))
            return await catalog.ResolveAsync(release, kind, profile.GameVersion, profile.Loader, ct);
        var loader = FabricService.LoaderFor(profile);
        var installed = new ResourceInstaller(profile.GameDirectory, downloads, profile.Loader, profile.GameVersion).List();
        var cache = Path.Combine(profile.GameDirectory, ".pcl-downloads");
        return profile.Loader == "quilt"
            ? await new QuiltModPlanner(catalog, downloads, cache, loader).ResolveAsync(release, profile.GameVersion, installed, profile.GameDirectory, progress, ct)
            : await new FabricModPlanner(catalog, downloads, cache, loader).ResolveAsync(release, profile.GameVersion, installed, profile.GameDirectory, progress, ct);
    }
}
