using Newtonsoft.Json.Linq;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // 路径选择必须来自当前实例重新扫描，安装计划和下载地址从 Host 保存的更新目录读取。
    private async Task<object?> ExecuteResourcesAsync(
        string id,
        PageInput request,
        JObject args,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        var profile = Profile(request.InstanceId);
        var local = new LocalResourceService(profile.GameDirectory, profile.Loader, profile.GameVersion);
        using var catalog = new ResourceCatalog(paths: source.Paths);
        var data = new DataPackService(profile.GameDirectory);
        string Text(string key) => (string? )args[key] ?? "";
        bool Flag(string key) => (bool? )args[key] ?? false;
        string[] Selected()
        {
            var values = args["paths"]?.ToObject<string[]>() ?? [];
            var current = local.Scan();
            if (values.Length == 0 || values.Any(
                path => !current.Any(
                    x => x.Path == path
                )
            ))
            {
                throw new OperationInstallException("RESOURCE_SELECTION_CHANGED");
            }

            return values;
        }

        LocalResourceUpdate Update(string selection)
        {
            var(key, index) = Selection(selection);
            var saved = Read<WorkspaceUpdates>(key, "workspace-updates");
            if (saved.InstanceId != profile.Id || index < 0 || index >= saved.Items.Length)
            {
                throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
            }

            return saved.Items[index];
        }

        WorldDirectory World() => data.Worlds()
            .SingleOrDefault(
                x => x.Path == Text("world")
            ) ?? throw new OperationInstallException("WORLD_SELECTION_CHANGED");
        WorldDataPack Pack(WorldDirectory world) => data.List(world)
            .SingleOrDefault(
                x => x.Path == Text("path")
            ) ?? throw new OperationInstallException("RESOURCE_SELECTION_CHANGED");
        switch (request.Method)
        {
            case "workspace.resources.scan":
                return new
                {
                    Items = local.Scan(),
                    ModNameStyle = source.Downloads.Tasks?.Options.ModNameStyle ?? "name"
                };
            case "workspace.resources.enable":
                local.SetEnabled(
                    Selected(),
                    Flag("enabled")
                );
                return true;
            case "workspace.resources.remove":
                var removal = new ShaderRemovalTransaction(profile.GameDirectory);
                removal.Recover();
                if (removal.Completed(id))
                {
                    return "已完成光影包卸载";
                }

                return local.Remove(
                    Selected(),
                    ct,
                    id
                );
            case "workspace.resources.validate":
                return local.ValidateEnabledResources();
            case "workspace.resources.history":
                return ResourceHistory.Read(profile.GameDirectory)
                    .Select(
                        x => x.ToString()
                    )
                    .ToArray();
            case "workspace.resources.undo":
                new ResourceInstaller(profile.GameDirectory, source.Downloads, profile.Loader, profile.GameVersion).UndoActivation(ct, id);
                return true;
            case "workspace.resources.updates":
                var updates = (await local.CheckUpdatesAsync(catalog, ct)).ToArray();
                Save(
                    id,
                    "workspace-updates",
                    new WorkspaceUpdates(profile.Id, updates)
                );
                return updates.Select(
                    (x, index) => new
                    {
                        SelectionId = id + ":" + index,
                        Current = x.Current,
                        Version = x.Available.Number
                    }
                )
                    .ToArray();
            case "workspace.resources.ignore":
                local.IgnoreUpdate(
                    Update(
                        Text("selection")
                    )
                        .Available.Id
                );
                return true;
            case "workspace.resources.update":
                await local.ApplyUpdateAsync(
                    Update(
                        Text("selection")
                    ),
                    catalog,
                    source.Downloads,
                    ct: ct
                );
                return true;
            case "workspace.resources.batchPrepare":
                if (workspaceBatches.Count >= 8)
                {
                    throw new OperationInstallException("BATCH_PREVIEW_LIMIT");
                }

                var items = (args["selections"]?.ToObject<string[]>() ?? []).Select(Update)
                    .ToArray();
                var prepared = await new ResourceBatchUpdater(profile.GameDirectory, profile.Loader, profile.GameVersion).PrepareAsync(items, catalog, source.Downloads, progress, ct);
                workspaceBatches.Add(id, (profile.Id, prepared));
                return new WorkspaceBatch(
                    id,
                    prepared.Changes.ToArray(),
                    prepared.Warnings.ToArray()
                );
            case "workspace.resources.batchApply":
            case "workspace.resources.batchDiscard":
                var handle = Text("handle");
                if (!workspaceBatches.TryGetValue(handle, out var batch) || batch.InstanceId != profile.Id)
                {
                    throw new OperationInstallException("BATCH_PREVIEW_EXPIRED");
                }

                try
                {
                    return request.Method == "workspace.resources.batchApply"
                        ? new ResourceBatchUpdater(profile.GameDirectory, profile.Loader, profile.GameVersion).Apply(batch.Batch, ct)
                        : "discarded";
                }
                finally
                {
                    batch.Batch.Dispose();
                    workspaceBatches.Remove(handle);
                }

            case "workspace.datapacks.worlds":
                return data.Worlds();
            case "workspace.datapacks.list":
                return data.List(
                    World()
                );
            case "workspace.datapacks.install":
                return await data.InstallAsync(
                    World(),
                    Text("source"),
                    ct
                );
            case "workspace.datapacks.enable":
                var world = World();
                data.SetEnabled(
                    world,
                    Pack(world),
                    Flag("enabled")
                );
                return true;
            case "workspace.datapacks.remove":
                var current = World();
                return data.Remove(
                    current,
                    Pack(current)
                );
            default:
                throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }
    }
}
