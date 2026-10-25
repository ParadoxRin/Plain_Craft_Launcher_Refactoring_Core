using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // Main-window use cases run in the existing owned operation lane, using the original installers.
    private async Task<object?> ExecuteDesktopAsync(string method, JObject args, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        string Text(string key) => (string? )args[key] ?? "";
        if (method == "workspace.desktop.versions")
        {
            return (await VersionCatalog.ListAsync(source, ct)).Select(
                v => new View.MinecraftRelease(v.Id, v.Type, v.Released)
            )
                .ToArray();
        }

        if (method == "workspace.desktop.macSetup")
        {
            return (await new MacSetupService(source).InstallAsync((bool? )args["shaders"] ?? false, progress, ct)).Profile;
        }

        if (method == "workspace.desktop.optiFabricValidate")
        {
            OptiFabricService.ValidateOptiFabricJar(
                Text("path")
            );
            return true;
        }

        if (method == "workspace.desktop.optiFabricInstall")
        {
            using var target = source.ForVersion(OptiFabricService.GameVersion);
            return await new OptiFabricService(target).InstallAsync(
                (string? )args["path"],
                Secret((string? )args["secretHandle"]),
                progress,
                ct
            );
        }

        var gameVersion = Text("gameVersion");
        VersionCatalog.ValidateId(gameVersion);
        var loader = Text("loader");
        if (loader is not ("fabric" or "quilt" or "forge" or "neoforge" or "optifine" or "liteloader"))
        {
            throw new OperationInstallException("LOADER_INVALID");
        }

        using var targetService = source.ForVersion(gameVersion);
        using var fabric = new FabricService(
            targetService,
            loader: loader == "quilt"
                ? "quilt"
                : "fabric"
        );
        using var forge = new ForgeService(targetService);
        using var optifine = new OptiFineService(targetService);
        using var lite = new LiteLoaderService(targetService);
        // Resolve available versions in Host; installation rechecks the selected version before touching the instance.
        var versions = loader switch
        {
            "fabric" or "quilt" => (await fabric.VersionsAsync(ct)).Select(
                x => x.Version
            )
                .ToArray(),
            "optifine" => (await optifine.VersionsAsync(ct)).Select(
                x => x.FileName
            )
                .ToArray(),
            "liteloader" => (await lite.VersionsAsync(ct)).Select(
                x => x.Build
            )
                .ToArray(),
            _ => (await forge.VersionsAsync(loader, ct)).Select(
                x => x.Version
            )
                .ToArray()
        };
        if (method == "workspace.desktop.loaderVersions")
        {
            return versions;
        }

        if (method != "workspace.desktop.loaderInstall")
        {
            throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }

        var selected = Text("version");
        if (!versions.Contains(selected, StringComparer.Ordinal))
        {
            throw new OperationInstallException("LOADER_VERSION_INVALID");
        }

        InstalledVersion parent;
        try
        {
            parent = targetService.LoadInstalled();
        }
        catch (Exception ex)when (ex is FileNotFoundException or InvalidDataException)
        {
            parent = await targetService.InstallAsync(progress, ct);
        }

        await new JavaService(targetService).EnsureForAsync(parent, progress, ct);
        return loader switch
        {
            "fabric" or "quilt" => await fabric.InstallAsync(selected, progress, ct),
            "optifine" => await optifine.InstallAsync(selected, progress, ct),
            "liteloader" => await lite.InstallAsync(selected, progress, ct),
            _ => await forge.InstallAsync(loader, selected, progress: progress, ct: ct)
        };
    }
}
