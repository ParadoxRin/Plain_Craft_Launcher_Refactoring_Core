using System.Text.Json;
using System.IO.Compression;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Contracts.Pages;
using View = Pcl.Core.Contracts.Workspaces;

internal static class ResourceFlow
{
    // 网络实测使用官方公开目录、真实版本文件和原依赖求解器；不需要外部服务密钥。
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var session = new LauncherSession(
            new HostClient(
                native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                native is null
                    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                    : "",
                root,
                reconnectable: true
            )
        );
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var fabric = await api.CallAsync<View.GameProfile>(
                "workspace.browser.ensureFabric",
                null,
                new
                {
                    gameVersion = "1.21.1",
                    selectedId = "1.21.1"
                }
            );
            Check(
                fabric.Loader == "fabric" && File.Exists(fabric.JsonPath),
                "host-prepares-java-and-fabric-for-default-resource-browser"
            );
            using var catalog = new ResourceCatalog(
                paths: new AppPaths(root)
            );
            var releases = await catalog.VersionsAsync("fabric-api", ResourceKind.Mod, "1.21.1", loader: "fabric");
            var older = releases.Where(
                x => x.Channel == "release"
            )
                .Skip(1)
                .First();
            await api.CallAsync<bool>(
                "workspace.browser.install",
                fabric.Id,
                new
                {
                    releaseId = older.Id,
                    kind = (int)ResourceKind.Mod
                }
            );
            var managed = await api.CallAsync<ManagedResource[]>("workspace.browser.list", fabric.Id);
            Check(
                managed.Any(
                    x => x.VersionId == older.Id
                ),
                "host-downloads-real-fabric-api-and-dependencies"
            );
            var available = await api.CallAsync<View.LocalResourceUpdate[]>("workspace.resources.updates", fabric.Id);
            Check(available.Length > 0, "host-resolves-compatible-local-mod-updates");
            var batch = await api.CallAsync<WorkspaceBatch>(
                "workspace.resources.batchPrepare",
                fabric.Id,
                new
                {
                    selections = available.Select(
                        x => x.SelectionId
                    )
                        .ToArray()
                }
            );
            Check(batch.Changes.Length > 0, "host-prepares-real-batch-download-and-dependency-plan");
            await api.CallAsync<string>(
                "workspace.resources.batchDiscard",
                fabric.Id,
                new
                {
                    handle = batch.Handle
                }
            );
            managed = await api.CallAsync<ManagedResource[]>("workspace.browser.list", fabric.Id);
            Check(
                managed.Any(
                    x => x.VersionId == older.Id
                ),
                "discarded-batch-keeps-current-mod-version"
            );
            batch = await api.CallAsync<WorkspaceBatch>(
                "workspace.resources.batchPrepare",
                fabric.Id,
                new
                {
                    selections = available.Select(
                        x => x.SelectionId
                    )
                        .ToArray()
                }
            );
            var backup = await api.CallAsync<string>(
                "workspace.resources.batchApply",
                fabric.Id,
                new
                {
                    handle = batch.Handle
                }
            );
            managed = await api.CallAsync<ManagedResource[]>("workspace.browser.list", fabric.Id);
            Check(
                !managed.Any(
                    x => x.VersionId == older.Id
                ) && Directory.Exists(backup),
                "host-commits-batch-update-and-preserves-old-files"
            );
            var mod = managed.Single(
                x => x.ProjectId == older.ProjectId
            );
            await api.CallAsync<bool>(
                "workspace.browser.enable",
                fabric.Id,
                new
                {
                    projectId = mod.ProjectId,
                    enabled = false
                }
            );
            Check(
                !(await api.CallAsync<ManagedResource[]>("workspace.browser.list", fabric.Id)).Single(
                    x => x.ProjectId == mod.ProjectId
                )
                    .Enabled,
                "browser-disable-command-uses-host"
            );
            await api.CallAsync<bool>(
                "workspace.browser.enable",
                fabric.Id,
                new
                {
                    projectId = mod.ProjectId,
                    enabled = true
                }
            );
            await api.CallAsync<string[]>("workspace.resources.validate", fabric.Id);
            var destination = Path.Combine(root, "fabric-api-export.jar");
            await api.ExportAsync(
                "workspace.browser.export",
                fabric.Id,
                new
                {
                    releaseId = older.Id
                },
                destination
            );
            Check(
                DownloadService.IsValid(
                    new(older.File.Url, destination, older.File.Size, older.File.Sha1)
                ),
                "host-resource-export-matches-official-hash"
            );
            var packFile = Path.Combine(root, "local-resource.zip");
            using (var zip = ZipFile.Open(packFile, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("pack.mcmeta")
                        .Open()
                );
                writer.Write("{\"pack\":{\"pack_format\":34,\"description\":\"Host fixture\"}}");
            }

            var pack = await api.CallAsync<ManagedResource>(
                "workspace.browser.import",
                fabric.Id,
                new
                {
                    path = packFile
                }
            );
            await api.CallAsync<bool>(
                "workspace.browser.activate",
                fabric.Id,
                new
                {
                    projectId = pack.ProjectId
                }
            );
            var instance = (await api.CallAsync<View.InstanceSnapshot>("workspace.instances.list", null)).Items.Single(
                x => x.Profile.Id == fabric.Id
            )
                .Profile;
            Check(
                File.ReadAllText(
                    Path.Combine(instance.GameDirectory, "options.txt")
                )
                    .Contains(pack.FileName),
                "browser-resource-pack-activation-writes-real-game-options"
            );
            await api.CallAsync<bool>("workspace.browser.undo", fabric.Id);
            await api.CallAsync<bool>(
                "workspace.browser.remove",
                fabric.Id,
                new
                {
                    projectId = pack.ProjectId
                }
            );
            Check(
                !(await api.CallAsync<ManagedResource[]>("workspace.browser.list", fabric.Id)).Any(
                    x => x.ProjectId == pack.ProjectId
                ),
                "browser-undo-and-remove-use-host"
            );
            // 断开连接后重新读取真实任务快照与资源列表，确保已提交操作不重放。
            await session.Client.DisconnectAsync();
            var after = await api.CallAsync<View.InstanceSnapshot>("workspace.instances.list", null);
            Check(
                after.Items.Any(
                    x => x.Profile.Id == fabric.Id
                ),
                "workspace-reconnect-restores-session-and-instance-view"
            );
            await session.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        fabricId = fabric.Id
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.Message
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
