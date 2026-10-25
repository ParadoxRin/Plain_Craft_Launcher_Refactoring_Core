using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // 浏览页只提交版本/项目标识；Host 从官方目录重新取得下载计划并复用原依赖求解、校验和事务。
    private async Task<object?> ExecuteBrowserAsync(
        string id,
        PageInput request,
        JObject args,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        var profile = Profile(request.InstanceId);
        using var catalog = new ResourceCatalog(paths: source.Paths);
        var store = new ResourceInstaller(profile.GameDirectory, source.Downloads, profile.Loader, profile.GameVersion);
        string Text(string name) => (string? )args[name] ?? "";
        Task<ResourceInstallPlan> Plan(ResourceRelease release, ResourceKind kind) => ResourceDependencyPlanner.ResolveAsync(catalog, source.Downloads, release, kind, profile, progress, ct);
        async Task<(ResourceRelease Release, ResourceInstallPlan Plan)?> Update(ManagedResource item)
        {
            if (item.ProjectId.StartsWith("local-") || item.ProjectId.StartsWith("curse-"))
            {
                return null;
            }

            var versions = await catalog.VersionsAsync(item.ProjectId, item.Kind, profile.GameVersion, loader: profile.Loader, ct: ct);
            foreach (var release in versions.Where(
                x => x.Channel == "release"
            ))
            {
                if (release.Id == item.VersionId)
                {
                    break;
                }

                try
                {
                    return (release, await Plan(release, item.Kind));
                }
                catch (InvalidDataException)
                {
                }
            }

            return null;
        }

        async Task Install(ResourceRelease release, ResourceKind kind)
        {
            var plan = await Plan(release, kind);
            var project = await catalog.ProjectAsync(release.ProjectId, ct);
            plan = plan with
            {
                Releases = plan.Releases.Select(
                    x => x.ProjectId == project.Id
                        ? x with
                        {
                            Name = project.Title
                        }
                        : x
                )
                    .ToArray()
            };
            if (kind == ResourceKind.Mod && release.ProjectId == IrisEnvironmentService.IrisProject)
            {
                await new IrisEnvironmentService(profile.GameDirectory, profile.Loader, profile.GameVersion).InstallAsync(plan, source.Downloads, progress, ct);
            }
            else
            {
                await store.InstallAsync(plan, progress, ct);
            }
        }

        switch (request.Method)
        {
            case "workspace.browser.export":
                var selected = await catalog.VersionAsync(
                    Text("releaseId"),
                    ct
                );
                var destination = WorkspaceOutput(id, selected.File.Name);
                await source.Downloads.DownloadAsync(
                    new(selected.File.Url, destination, selected.File.Size, selected.File.Sha1),
                    ct
                );
                return RegisterWorkspaceExport(id, destination);
            case "workspace.browser.irisPreview":
                return new IrisEnvironmentService(profile.GameDirectory, profile.Loader, profile.GameVersion).Preview();
            case "workspace.browser.irisUninstall":
                return new IrisEnvironmentService(profile.GameDirectory, profile.Loader, profile.GameVersion).Uninstall(
                    Text("fingerprint"),
                    id,
                    ct
                );
            case "workspace.browser.backends":
                return ShaderBackendInspection.Describe(profile.GameDirectory, profile.Loader, profile.GameVersion);
            case "workspace.browser.display":
                return store.List()
                    .Select(
                        x => new Pcl.Core.Contracts.Workspaces.ManagedResource(
                            x.ProjectId,
                            x.VersionId,
                            x.Name,
                            x.Version,
                            (Pcl.Core.Contracts.Workspaces.ResourceKind)x.Kind,
                            x.FileName,
                            x.Size,
                            x.Enabled,
                            LocalResourcePreferences.Display(
                                x,
                                NetworkOptions.Load(source.Paths)
                                    .ModNameStyle
                            )
                        )
                    )
                    .ToArray();
            case "workspace.browser.list":
                return store.List();
            case "workspace.browser.enable":
                store.SetEnabled(
                    Text("projectId"),
                    (bool? )args["enabled"] ?? false
                );
                return true;
            case "workspace.browser.remove":
                store.Remove(
                    Text("projectId"),
                    ct,
                    id
                );
                return true;
            case "workspace.browser.activate":
                store.Activate(
                    Text("projectId")
                );
                return true;
            case "workspace.browser.undo":
                store.UndoActivation(ct, id);
                return true;
            case "workspace.browser.import":
                var path = Text("path");
                var kind = ResourceKind.Mod;
                if (Path.GetExtension(path)
                    .Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(path);
                    kind = zip.Entries.Any(
                        x => x.FullName.StartsWith("shaders/", StringComparison.Ordinal)
                    )
                        ? ResourceKind.Shader
                        : ResourceKind.ResourcePack;
                }

                return await store.ImportAsync(path, kind, ct);
            case "workspace.browser.install":
                var selectedKind = (ResourceKind)(int)(args["kind"] ?? throw new OperationInstallException("RESOURCE_KIND_REQUIRED"));
                if (selectedKind is not (ResourceKind.Mod or ResourceKind.Shader or ResourceKind.ResourcePack))
                {
                    throw new OperationInstallException("RESOURCE_KIND_INVALID");
                }

                await Install(
                    await catalog.VersionAsync(
                        Text("releaseId"),
                        ct
                    ),
                    selectedKind
                );
                return true;
            case "workspace.browser.iris":
                if (profile.Loader != "fabric")
                {
                    throw new InvalidDataException("当前光影方案需要 Fabric / Iris，请先选择 Fabric 实例。");
                }

                if (store.List()
                    .Any(
                        x => x.ProjectId == "YL57xq9U" && x.Enabled
                    ))
                {
                    return true;
                }

                var releases = await catalog.VersionsAsync("iris", ResourceKind.Mod, profile.GameVersion, loader: profile.Loader, ct: ct);
                await Install(
                    releases.FirstOrDefault(
                        x => x.Channel == "release"
                    ) ?? releases.First(),
                    ResourceKind.Mod
                );
                return true;
            case "workspace.browser.update":
                var item = store.List()
                    .Single(
                        x => x.ProjectId == Text("projectId")
                    );
                var update = await Update(item);
                if (update is null)
                {
                    return "暂无兼容更新";
                }

                await Install(update.Value.Release, item.Kind);
                return "已更新 · " + update.Value.Release.Name;
            case "workspace.browser.updates":
                var changes = new List<string>();
                foreach (var itemNow in store.List())
                {
                    var newer = await Update(itemNow);
                    if (newer is not null)
                    {
                        changes.Add(itemNow.Name + " → " + newer.Value.Release.Number);
                    }
                }

                return changes;
            case "workspace.browser.datapack":
                var data = new DataPackService(profile.GameDirectory);
                var world = data.Worlds()
                    .Single(
                        x => x.Path == Text("world")
                    );
                var release = await catalog.VersionAsync(
                    Text("releaseId"),
                    ct
                );
                var dataPlan = await catalog.ResolveAsync(release, ResourceKind.DataPack, profile.GameVersion, ct: ct);
                await data.InstallPlanAsync(world, dataPlan, source.Downloads, progress, ct);
                return true;
            default:
                throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }
    }
}
