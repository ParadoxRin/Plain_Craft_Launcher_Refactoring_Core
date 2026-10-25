using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
public sealed record WorkspaceCheckpoint(string Fingerprint, string State, string? Result);
public sealed record WorkspacePackSource(string Path, string Sha256);
public sealed record WorkspaceScan(string Root, InstanceImportCandidate[] Items);
public sealed record WorkspaceUpdates(string InstanceId, LocalResourceUpdate[] Items);
/// <summary>工作区执行适配：只调已登记的原业务流程；写入前留收据，未知提交拒绝重复执行。</summary>
public sealed partial class PageExecutionAdapter
{
    // 来源只用于展示，不能作为可执行路径授权；缺失或旧版本记录不影响实例使用。
    private static string? ComponentOrigin(string gameDirectory)
    {
        var receipt = ResourceInstaller.SafeTarget(gameDirectory, "pcl-component-origin.json");
        if (!File.Exists(receipt))
        {
            return null;
        }

        try
        {
            return (string? )JsonFiles.Read(receipt)["sourceDirectory"];
        }
        catch (Exception ex)when (ex is IOException or Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private readonly Dictionary<string, (string Value, DateTimeOffset Expires)> workspaceSecrets = new();
    private readonly Dictionary<string, (string InstanceId, PreparedResourceBatch Batch)> workspaceBatches = new();
    public string RememberSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192)
        {
            throw new OperationInstallException("SECRET_INPUT_INVALID");
        }

        lock (workspaceSecrets)
        {
            foreach (var key in workspaceSecrets.Where(
                x => x.Value.Expires < DateTimeOffset.UtcNow
            )
                .Select(
                    x => x.Key
                )
                .ToArray())
            {
                workspaceSecrets.Remove(key);
            }

            if (workspaceSecrets.Count >= 16)
            {
                throw new OperationInstallException("SECRET_LIMIT");
            }

            var id = "secret-" + Guid.NewGuid()
                .ToString("N");
            workspaceSecrets[id] = (value, DateTimeOffset.UtcNow.AddMinutes(30));
            return id;
        }
    }

    private string? Secret(string? handle, bool consume = false)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return null;
        }

        lock (workspaceSecrets)
        {
            if (!workspaceSecrets.TryGetValue(handle, out var row) || row.Expires < DateTimeOffset.UtcNow)
            {
                throw new OperationInstallException("SECRET_REENTRY_REQUIRED");
            }

            if (consume)
            {
                workspaceSecrets.Remove(handle);
            }

            return row.Value;
        }
    }

    public void Dispose()
    {
        foreach (var row in workspaceBatches.Values)
        {
            row.Batch.Dispose();
        }

        foreach (var link in links.Values)
        {
            link.Process.Dispose();
        }

        links.Clear();
        workspaceBatches.Clear();
        searchSessions.Clear();
        externalChallenges.Clear();
        lock (workspaceSecrets)
        {
            workspaceSecrets.Clear();
        }
    }

    private static string TrackingId(string id) => "op-" + Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes("workspace:" + id)
        )
    )
        .ToLowerInvariant()[..32];
    private async Task<byte[]> ExecuteWorkspaceAsync(string id, PageInput request, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var tracking = TrackingId(id);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request)
            )
        );
        if (records.TryGetValue(tracking, out var prior))
        {
            var checkpoint = Read<WorkspaceCheckpoint>(tracking, "workspace");
            if (checkpoint.Fingerprint != fingerprint)
            {
                throw new OperationInstallException("WORKSPACE_INPUT_CHANGED");
            }

            if (checkpoint.State == "completed")
            {
                return Encoding.UTF8.GetBytes(checkpoint.Result!);
            }

            if (!WorkspaceMethods.Reads.Contains(request.Method))
            {
                throw new OperationInstallException("WORKSPACE_REVIEW_REQUIRED");
            }
        }

        Save(
            tracking,
            "workspace",
            new WorkspaceCheckpoint(fingerprint, "started", null)
        );
        var args = JObject.Parse(request.PayloadJson ?? "{}");
        string Text(string key) => (string? )args[key] ?? "";
        bool Flag(string key) => (bool? )args[key] ?? false;
        T Value<T>(string key)
        where T : class => args[key]?.ToObject<T>() ?? throw new OperationInstallException("WORKSPACE_ARGUMENT_INVALID");
        GameProfile ProfileNow() => Profile(request.InstanceId);
        var manager = new InstanceManager(source);
        object? result;
        progress.Report(
            new("执行工作区任务", 0, 1, 0, 0)
        );
        switch (request.Method)
        {
            case "workspace.browser.ensureFabric":
                // 默认资源页的原版/Java/Fabric 准备同样在 Host 中执行，不能遗留 UI 安装旁路。
                var selectedId = (string? )args["selectedId"];
                if (selectedId is not null && Profile(selectedId)is { Loader: not "vanilla" } selectedProfile)
                {
                    result = selectedProfile;
                    break;
                }

                using (var target = source.ForVersion(
                    Text("gameVersion")
                ))
                {
                    InstalledVersion parent;
                    try
                    {
                        parent = target.LoadInstalled();
                    }
                    catch (Exception ex)when (ex is FileNotFoundException or InvalidDataException)
                    {
                        parent = await target.InstallAsync(progress, ct);
                    }

                    await new JavaService(target).EnsureForAsync(parent, progress, ct);
                    using var fabric = new FabricService(target);
                    result = fabric.Profiles()
                        .FirstOrDefault(
                            x => x.GameDirectory == fabric.ProfileRoot
                        ) ?? await fabric.InstallAsync(progress: progress, ct: ct);
                }

                break;
            case "workspace.instances.list":
                result = new
                {
                    Items = manager.List()
                        .Select(
                            p => new
                            {
                                Profile = p,
                                Preferences = manager.Settings.Preferences(p.GameDirectory),
                                Effective = manager.Settings.Effective(p.GameDirectory),
                                Fields = manager.Settings.Patch(p.GameDirectory)?.Fields ?? [],
                                Shared = Path.GetFullPath(p.GameDirectory) == Path.GetFullPath(source.Paths.Game),
                                OriginDirectory = ComponentOrigin(p.GameDirectory)
                            }
                        )
                        .ToArray(),
                    Global = manager.Settings.Global,
                    Architecture = source.Platform.Architecture
                };
                break;
            case "workspace.instances.preferences":
                result = manager.Settings.Preferences(
                    ProfileNow()
                        .GameDirectory
                );
                break;
            case "workspace.instances.savePreferences":
                manager.Settings.SavePreferences(
                    ProfileNow()
                        .GameDirectory,
                    Value<InstancePreferences>("value")
                );
                result = true;
                break;
            case "workspace.instances.settings":
                var selected = request.InstanceId is null
                    ? null
                    : ProfileNow();
                result = new
                {
                    Global = manager.Settings.Global,
                    Effective = selected is null
                        ? manager.Settings.Global
                        : manager.Settings.Effective(selected.GameDirectory),
                    Patch = selected is null
                        ? null
                        : manager.Settings.Patch(selected.GameDirectory)
                };
                break;
            case "workspace.instances.saveSettings":
                if (request.InstanceId is null)
                {
                    manager.Settings.SaveGlobal(
                        Value<LaunchSettings>("value")
                    );
                }
                else
                {
                    manager.Settings.SavePatch(
                        ProfileNow()
                            .GameDirectory,
                        LaunchSettingsPatch.From(
                            Value<LaunchSettings>("value"),
                            args["fields"]?.ToObject<string[]>() ?? []
                        )
                    );
                }

                result = true;
                break;
            case "workspace.java.inspect":
                result = await JavaService.InspectAsync(
                    Text("path"),
                    ct
                );
                break;
            case "workspace.java.discover":
                result = await JavaDiscovery.InspectAsync(
                    source.Paths,
                    args["paths"]?.ToObject<string[]>() ?? [],
                    ct
                );
                break;
            case "workspace.instances.scan":
                var scan = new WorkspaceScan(
                    Path.GetFullPath(
                        Text("path")
                    ),
                    manager.Scan(
                        Text("path")
                    )
                        .ToArray()
                );
                Save(id, "workspace-scan", scan);
                result = new
                {
                    Handle = id,
                    Items = scan.Items
                };
                break;
            case "workspace.instances.import":
                var(scanId, scanIndex) = Selection(
                    Text("selection")
                );
                var scanned = Read<WorkspaceScan>(scanId, "workspace-scan");
                if (scanIndex < 0 || scanIndex >= scanned.Items.Length)
                {
                    throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
                }

                result = await manager.ImportAsync(scanned.Items[scanIndex], progress, ct);
                break;
            case "workspace.instances.copy":
                result = await manager.CopyAsync(
                    ProfileNow(),
                    Text("name"),
                    ct
                );
                break;
            case "workspace.instances.repair":
                await manager.RepairAsync(
                    ProfileNow(),
                    progress,
                    ct
                );
                result = true;
                break;
            case "workspace.instances.trash":
                result = manager.DeleteToTrash(
                    ProfileNow()
                );
                break;
            case "workspace.instances.listTrash":
                result = manager.ListTrash();
                break;
            case "workspace.instances.restoreTrash":
                result = manager.RestoreTrash(
                    Text("id")
                );
                break;
            case "workspace.instances.offlineScript":
                var scriptPath = WorkspaceOutput(id, "Minecraft-offline.command");
                var offlineName = AccountManager.ValidateOfflineName(
                    Text("playerName")
                );
                var offlineAccount = PageAccounts.Accounts.FirstOrDefault(
                    x => x.Kind == AccountKind.Offline && x.Name == offlineName
                );
                await manager.ExportOfflineScriptAsync(
                    ProfileNow(),
                    scriptPath,
                    offlineName,
                    ct,
                    offlineAccount
                );
                result = RegisterWorkspaceExport(id, scriptPath, executable: true);
                break;
            case "workspace.instances.backup":
                var backup = WorkspaceOutput(id, "instance-backup.zip");
                await manager.BackupAsync(
                    ProfileNow(),
                    backup,
                    ct
                );
                result = RegisterWorkspaceExport(id, backup);
                break;
            case "workspace.instances.verifyBackup":
                result = await InstanceBackupArchive.VerifyAsync(
                    Text("path"),
                    ct
                );
                break;
            case "workspace.instances.restoreBackup":
                result = await manager.RestoreBackupAsync(
                    Text("path"),
                    progress,
                    ct
                );
                break;
            case "workspace.instances.icon":
                var icon = InstanceIconService.Read(
                    ProfileNow()
                        .GameDirectory
                );
                if (icon is null)
                {
                    result = null;
                    break;
                }

                var iconPath = WorkspaceOutput(id, "instance.png");
                await File.WriteAllBytesAsync(iconPath, icon, ct);
                result = RegisterWorkspaceExport(id, iconPath);
                break;
            case "workspace.instances.saveIcon":
                var imagePath = Text("path");
                if (new FileInfo(imagePath).Length > InstanceIconService.MaximumBytes)
                {
                    throw new OperationInstallException("IMAGE_TOO_LARGE");
                }

                new InstanceIconService(source.Paths).SavePng(
                    ProfileNow(),
                    await File.ReadAllBytesAsync(imagePath, ct)
                );
                result = true;
                break;
            case "workspace.instances.removeIcon":
                new InstanceIconService(source.Paths).Remove(
                    ProfileNow()
                );
                result = true;
                break;
            case "workspace.packs.download":
                using (var catalog = new ResourceCatalog(paths: source.Paths))
                {
                    var release = await catalog.VersionAsync(
                        Text("releaseId"),
                        ct
                    );
                    var packFile = await new ModpackService(source).DownloadReleaseAsync(release, progress, ct);
                    var packPlan = PortableModpackService.Inspect(
                        packFile,
                        new(IncludeOptional: true)
                    );
                    var packHash = HashFile(packFile);
                    Save(
                        id,
                        "workspace-pack",
                        new WorkspacePackSource(packFile, packHash)
                    );
                    result = new
                    {
                        Handle = id,
                        Plan = packPlan,
                        Digest = packHash
                    };
                }

                break;
            case "workspace.packs.inspect":
                var archive = Path.GetFullPath(
                    Text("path")
                );
                var plan = PortableModpackService.Inspect(
                    archive,
                    PackOptions(args)with
                    {
                        IncludeOptional = true,
                        SelectedOptionalFiles = null
                    }
                );
                var digest = HashFile(archive);
                Save(
                    id,
                    "workspace-pack",
                    new WorkspacePackSource(archive, digest)
                );
                result = new
                {
                    Handle = id,
                    Plan = plan,
                    Digest = digest
                };
                break;
            case "workspace.packs.install":
            case "workspace.packs.update":
                var pack = Read<WorkspacePackSource>(
                    Text("handle"),
                    "workspace-pack"
                );
                if (HashFile(pack.Path) != pack.Sha256)
                {
                    throw new OperationInstallException("PACK_PREVIEW_CHANGED");
                }

                var portable = new PortableModpackService(source);
                var options = PackOptions(args)with
                {
                    CurseForgeApiKey = Secret((string? )args["secretHandle"])
                };
                result = request.Method == "workspace.packs.install"
                    ? await portable.InstallAsync(
                        pack.Path,
                        options,
                        "workspace-pack-" + Id(id),
                        progress,
                        ct
                    )
                    : await portable.UpdateAsNewInstanceAsync(
                        ProfileNow(),
                        pack.Path,
                        options,
                        Flag("preserve"),
                        progress,
                        ct
                    );
                break;
            case "workspace.packs.changes":
                result = new
                {
                    Changed = PortableModpackService.ChangedUserFiles(
                        ProfileNow()
                    ),
                    Deleted = PortableModpackService.DeletedUserFiles(
                        ProfileNow()
                    )
                };
                break;
            case "workspace.packs.exportOptions":
                result = PortableModpackService.LoadExportOptions(
                    ProfileNow()
                );
                break;
            case "workspace.packs.saveExportOptions":
                PortableModpackService.SaveExportOptions(
                    ProfileNow(),
                    Value<PackExportOptions>("options")
                );
                result = true;
                break;
            case "workspace.packs.export":
                var export = WorkspaceOutput(id, "instance.mrpack");
                await PortableModpackService.ExportAsync(
                    ProfileNow(),
                    export,
                    Value<PackExportOptions>("options"),
                    ct
                );
                result = RegisterWorkspaceExport(id, export);
                break;
            default:
                result = request.Method.StartsWith("workspace.shell.", StringComparison.Ordinal)
                    ? await ExecuteShellAsync(id, request.Method, args, ct)
                    : request.Method.StartsWith("workspace.desktop.", StringComparison.Ordinal)
                        ? await ExecuteDesktopAsync(request.Method, args, progress, ct)
                        : request.Method.StartsWith("workspace.link.", StringComparison.Ordinal)
                            ? await ExecuteLinkAsync(id, request.Method, args, ct)
                            : request.Method.StartsWith("workspace.catalog.", StringComparison.Ordinal)
                                ? await ExecuteCatalogAsync(id, request.Method, args, ct)
                                : request.Method.StartsWith("workspace.account.", StringComparison.Ordinal)
                                    ? await ExecuteAccountPageAsync(id, request.Method, args, ct)
                                    : request.Method.StartsWith("workspace.search.", StringComparison.Ordinal)
                                        ? await ExecuteSearchAsync(id, request, args, progress, ct)
                                        : request.Method.StartsWith("workspace.settings.", StringComparison.Ordinal)
                                            ? await ExecuteSettingsAsync(id, args, request.Method, ct)
                                            : request.Method.StartsWith("workspace.browser.", StringComparison.Ordinal)
                                                ? await ExecuteBrowserAsync(id, request, args, progress, ct)
                                                : await ExecuteResourcesAsync(id, request, args, progress, ct);
                break;
        }

        var serialized = JsonConvert.SerializeObject(result);
        if (Encoding.UTF8.GetByteCount(serialized) > 524288)
        {
            throw new OperationInstallException("PAGE_RESULT_TOO_LARGE");
        }

        Save(
            tracking,
            "workspace",
            new WorkspaceCheckpoint(fingerprint, "completed", serialized)
        );
        progress.Report(
            new("工作区任务完成", 1, 1, 0, 0)
        );
        return Encoding.UTF8.GetBytes(serialized);
    }

    private static PackImportOptions PackOptions(JObject args)
    {
        var options = args["options"]?.ToObject<PackImportOptions>() ?? new();
        // 密钥只由 Host 内存句柄补入，永不读持久化请求中的明文密钥。
        return options with
        {
            CurseForgeApiKey = null
        };
    }

    private string WorkspaceOutput(string id, string name)
    {
        var path = Safe(
            "page-artifacts/" + Id(id) + "/" + name
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        if (File.Exists(path))
        {
            throw new OperationInstallException("WORKSPACE_REVIEW_REQUIRED");
        }

        return path;
    }

    private ArtifactExport RegisterWorkspaceExport(string id, string path, bool executable = false)
    {
        var sha = HashFile(path);
        var size = new FileInfo(path).Length;
        Save(
            id,
            "export",
            new ControlledExport(path, size, sha, "local_sha256")
        );
        return new(
            Path.GetFileName(path),
            size,
            sha,
            "local_sha256",
            executable
        );
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(
            SHA256.HashData(input)
        );
    }

    private static (string Id, int Index) Selection(string selection)
    {
        var parts = selection.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
        {
            throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
        }

        return (parts[0], index);
    }
}
