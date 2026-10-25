using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
public sealed record PrivatePageIdentity(string OperationId);
public sealed record PrivatePageRecord(int FormatVersion, PrivatePageIdentity Snapshot, string Kind, JsonElement Payload);
public sealed record ArtifactCatalog(string GameVersion, DownloadArtifact[] Files);
public sealed record UsedComponentPlan(string ExecutionId, ComponentMigrationPlan Plan);
public sealed record ControlledExport(string Path, long Size, string Sha256, string Verification);
/// <summary>Host 内复用旧组件/官方文件流程；私有计划与下载目录只以受控 ID 暴露。</summary>
public sealed partial class PageExecutionAdapter : IPageExecutor, IDisposable
{
    private readonly MinecraftService source;
    private AccountManager? pageAccounts;
    private readonly IOperationStore privateStore;
    private readonly ConcurrentDictionary<string, PrivatePageRecord> records = new();
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public PageExecutionAdapter(MinecraftService source, IOperationStore privateStore, AccountManager? accounts = null)
    {
        this.source = source;
        pageAccounts = accounts;
        this.privateStore = privateStore;
        foreach (var bytes in privateStore.ReadAll())
        {
            var row = JsonSerializer.Deserialize<PrivatePageRecord>(bytes) ?? throw new InvalidDataException("PAGE_PRIVATE_STORE_INVALID");
            if (row.FormatVersion != 1 || row.Kind is not ("plan" or "plan-used" or "catalog" or "export" or "workspace" or "workspace-pack" or "workspace-scan" or "workspace-updates" or "search-hits" or "search-releases" or "cache-preview" or "browser-projects" or "public-skin"))
            {
                throw new InvalidDataException("PAGE_PRIVATE_STORE_INVALID");
            }

            records[row.Snapshot.OperationId] = row;
        }
    }

    public async Task<byte[]> ExecuteAsync(
        string executionId,
        PageInput input,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (WorkspaceMethods.Known.Contains(input.Method))
        {
            return await ExecuteWorkspaceAsync(executionId, input, progress, ct);
        }

        object? result;
        var components = new InstanceComponentService(source);
        switch (input.Method)
        {
            case "instance.list":
                result = InstanceRegistry.List(source, true)
                    .Select(
                        x => new ComponentSource(x.Id, x.Name, x.GameVersion, x.Loader)
                    )
                    .ToArray();
                break;
            case "component.versions":
                result = await components.VersionsAsync(
                    Profile(input.InstanceId),
                    input.Loader ?? "vanilla",
                    ct
                );
                break;
            case "component.confirmation":
                ComponentConfirmationPreference.Save(source.Paths.Root, input.DisableMods);
                result = true;
                break;
            case "component.preview":
                var plan = components.Preview(
                    Profile(input.InstanceId),
                    input.Loader ?? "vanilla",
                    input.Version,
                    input.DisableMods,
                    ct
                );
                // 仅创建启动器的实例容器目录，便于确认页打开父目录；此时不创建目标实例或执行安装。
                Directory.CreateDirectory(
                    Safe("instances")
                );
                plan = plan with
                {
                    TargetKey = Id(executionId)
                };
                Save(executionId, "plan", plan);
                result = new ComponentPreview(
                    executionId,
                    plan.TargetLoader,
                    plan.TargetVersion,
                    plan.DisableMods,
                    plan.Files.Count,
                    plan.ActiveMods.ToArray(),
                    plan.Conflicts.ToArray()
                )
                {
                    SourceDirectory = plan.Source.GameDirectory,
                    TargetDirectory = Safe("instances/modified-" + plan.TargetKey),
                    SuppressConfirmation = ComponentConfirmationPreference.Read(source.Paths.Root)
                };
                break;
            case "component.apply":
                ComponentMigrationPlan selected;
                if (input.PlanId is { } planId && records.TryGetValue(planId, out var planRecord) && planRecord.Kind == "plan-used")
                {
                    var used = Read<UsedComponentPlan>(planId, "plan-used");
                    if (used.ExecutionId != executionId)
                    {
                        throw new OperationInstallException("COMPONENT_PLAN_ALREADY_APPLIED");
                    }

                    selected = used.Plan;
                }
                else
                {
                    selected = Read<ComponentMigrationPlan>(input.PlanId, "plan");
                }

                if (input.InstanceId != selected.Source.Id || selected.Source != Profile(selected.Source.Id))
                {
                    throw new OperationInstallException("COMPONENT_SOURCE_CHANGED");
                }

                result = await ApplyAsync(executionId, selected, components, progress, ct);
                // 完成后绑定计划与执行身份；同一操作可恢复回执，新操作必须重新预览。
                Save(
                    input.PlanId!,
                    "plan-used",
                    new UsedComponentPlan(executionId, selected)
                );
                break;
            case "artifact.list":
                using (var target = source.ForVersion(input.GameVersion ?? ""))
                    using (var download = new ArtifactDownloadService(target))
                    {
                        var files = (await download.ListAsync(input.Loader ?? "client", ct)).ToArray();
                        Save(
                            executionId,
                            "catalog",
                            new ArtifactCatalog(target.GameVersion, files)
                        );
                        result = files.Select(
                            (file, index) => new ArtifactChoice(executionId + ":" + index, file.Version, file.FileName)
                        )
                            .ToArray();
                    }

                break;
            case "artifact.download":
                result = await DownloadAsync(executionId, input.SelectionId, progress, ct);
                break;
            case "artifact.notes":
                using (var target = source.ForVersion(input.GameVersion ?? ""))
                    using (var download = new ArtifactDownloadService(target))
                    {
                        var note = await download.NotesAsync(ct);
                        result = note is null
                            ? null
                            : new ArtifactNotes(note.Title, note.Text);
                    }

                break;
            default:
                throw new OperationInstallException("PAGE_METHOD_UNSUPPORTED");
        }

        return JsonSerializer.SerializeToUtf8Bytes(result, Wire);
    }

    // 固定操作身份避免重放复制出多个实例；无法确认的提交窗口保留现场并拒绝重复发布。
    private async Task<ComponentResult> ApplyAsync(
        string id,
        ComponentMigrationPlan plan,
        InstanceComponentService components,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        var key = plan.TargetKey ?? Id(id);
        var target = Safe("instances/modified-" + key);
        var backupFolder = Safe("instance-component-backups/" + key);
        var journal = Safe("instance-component-backups/" + key + "/migration.json");
        if (Directory.Exists(target))
        {
            if (!File.Exists(journal) || (string? )JsonFiles.Read(journal)["status"] != "committed")
            {
                throw new OperationInstallException("COMPONENT_COMMIT_REVIEW_REQUIRED");
            }

            var receipt = JsonFiles.Read(journal);
            var restored = receipt["target"]?.ToObject<GameProfile>() ?? throw new InvalidDataException("COMPONENT_RECEIPT_INVALID");
            if (restored.Id != "modified-" + key || Path.GetFullPath(restored.GameDirectory) != target || (string? )receipt["plan"]?["Source"]?["Id"] != plan.Source.Id)
            {
                throw new InvalidDataException("COMPONENT_RECEIPT_INVALID");
            }

            VerifyPublished(restored, plan, ct);
            return new(
                restored.Id,
                restored.Name,
                Path.Combine(backupFolder, "components.zip"),
                journal,
                plan.DisableMods
                    ? plan.ActiveMods.Count
                    : 0
            )
            {
                SourceDirectory = plan.Source.GameDirectory,
                TargetDirectory = target
            };
        }

        // 尚未发布的旧暂存/备份移到独立历史目录，重新准备前再校验原计划；绝不删除源实例。
        var pending = Safe("instances/pending-pack-component-" + key);
        if (Directory.Exists(pending))
        {
            Directory.Move(
                pending,
                Safe(
                    "instances/.interrupted-component-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
        }

        if (Directory.Exists(backupFolder))
        {
            Directory.Move(
                backupFolder,
                Safe(
                    "instance-component-backups/" + key + "-history-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
        }

        try
        {
            var result = await components.ApplyAsync(plan, progress, ct, key);
            return new(
                result.Profile.Id,
                result.Profile.Name,
                result.Backup,
                result.Receipt,
                result.DisabledMods
            )
            {
                SourceDirectory = plan.Source.GameDirectory,
                TargetDirectory = result.Profile.GameDirectory
            };
        }
        catch (InvalidDataException ex)when (ex.Message == "源实例已变化，请重新预览。")
        {
            throw new OperationInstallException("COMPONENT_SOURCE_CHANGED");
        }
    }

    // 成功回执只能证明曾提交；接回之前还要核对当前实例与复制数据，不能采信失效现场。
    private void VerifyPublished(GameProfile profile, ComponentMigrationPlan plan, CancellationToken ct)
    {
        try
        {
            if (profile.GameVersion != plan.Source.GameVersion || profile.Loader != plan.TargetLoader || profile.JsonPath != Path.Combine(profile.GameDirectory, "version.json") || Profile(profile.Id) != profile)
            {
                throw new InvalidDataException();
            }

            var installed = InstanceRegistry.Load(source, profile);
            if (!File.Exists(installed.JarPath) || installed.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
            {
                throw new InvalidDataException();
            }

            foreach (var entry in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                var relative = entry.RelativePath + (entry.ActiveMod && plan.DisableMods
                    ? ".disabled"
                    : "");
                var path = ResourceInstaller.SafeTarget(profile.GameDirectory, relative);
                if (!File.Exists(path))
                {
                    throw new InvalidDataException();
                }

                if (plan.DisableMods && entry.RelativePath == ".pcl-resources.json")
                {
                    // 此清单由旧安装流程合法改写为禁用模组，验证其结构与禁用状态。
                    var resources = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ManagedResource>>(
                        File.ReadAllText(path)
                    ) ?? throw new InvalidDataException();
                    if (resources.Any(
                        x => x.Kind == ResourceKind.Mod && x.Enabled
                    ))
                    {
                        throw new InvalidDataException();
                    }

                    continue;
                }

                using var input = File.OpenRead(path);
                if (input.Length != entry.Size || !Convert.ToHexString(
                    SHA256.HashData(input)
                )
                    .Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException();
                }
            }
        }
        catch (Exception ex)when (ex is InvalidDataException or IOException or OperationInstallException)
        {
            throw new OperationInstallException("COMPONENT_COMMIT_REVIEW_REQUIRED");
        }
    }

    private async Task<ArtifactExport> DownloadAsync(string id, string? selection, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (records.TryGetValue(id, out var existing) && existing.Kind == "export")
        {
            var export = OpenExport(id);
            using var verify = File.OpenRead(export.Path);
            if (Convert.ToHexString(
                SHA256.HashData(verify)
            )
                .Equals(export.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    Path.GetFileName(export.Path),
                    export.Size,
                    export.Sha256,
                    export.Verification
                );
            }

            throw new OperationInstallException("EXPORT_INTEGRITY_FAILED");
        }

        var parts = selection?.Split(':') ?? [];
        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
        {
            throw new OperationInstallException("ARTIFACT_SELECTION_INVALID");
        }

        var catalog = Read<ArtifactCatalog>(parts[0], "catalog");
        if (index < 0 || index >= catalog.Files.Length)
        {
            throw new OperationInstallException("ARTIFACT_SELECTION_INVALID");
        }

        var file = catalog.Files[index];
        if (Path.GetFileName(file.FileName) != file.FileName || file.FileName.Contains('\\'))
        {
            throw new InvalidDataException("ARTIFACT_NAME_INVALID");
        }

        var destination = Safe(
            "page-artifacts/" + Id(id) + "/" + file.FileName
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!
        );
        if (File.Exists(destination))
        {
            File.Move(
                destination,
                destination + ".unconfirmed-" + Guid.NewGuid()
                    .ToString("N")
            );
        }

        progress.Report(
            new("下载并校验官方文件", 0, 1, 0, file.Size)
        );
        using var target = source.ForVersion(catalog.GameVersion);
        using var downloads = new ArtifactDownloadService(target);
        var saved = await downloads.SaveAsync(file, destination, ct);
        Save(
            id,
            "export",
            new ControlledExport(destination, saved.Size, saved.Sha256, saved.Verification)
        );
        progress.Report(
            new("文件已校验", 1, 1, saved.Size, saved.Size)
        );
        return new(file.FileName, saved.Size, saved.Sha256, saved.Verification);
    }

    // 只允许打开本操作的私有导出，不接受 RPC 路径；接收方再校验完整 SHA-256。
    public ControlledExport OpenExport(string id)
    {
        var saved = Read<ControlledExport>(id, "export");
        var expected = Safe(
            "page-artifacts/" + Id(id) + "/" + Path.GetFileName(saved.Path)
        );
        if (saved.Path != expected || !File.Exists(expected) || new FileInfo(expected).Length != saved.Size)
        {
            throw new OperationInstallException("EXPORT_INTEGRITY_FAILED");
        }

        return saved;
    }

    private GameProfile Profile(string? id) => InstanceRegistry.List(source, true)
        .SingleOrDefault(
            x => x.Id == id
        ) ?? throw new OperationInstallException("INSTANCE_NOT_FOUND");
    private string Safe(string relative) => ResourceInstaller.SafeTarget(source.Paths.Root, relative);
    private static string Id(string value)
    {
        if (!value.StartsWith("op-", StringComparison.Ordinal) || !Guid.TryParseExact(value[3..], "N", out _))
        {
            throw new OperationInstallException("PAGE_HANDLE_INVALID");
        }

        return value[3..];
    }

    private T Read<T>(string? id, string kind)
    {
        if (id is null || !records.TryGetValue(id, out var row) || row.Kind != kind)
        {
            throw new OperationInstallException("PAGE_HANDLE_EXPIRED_OR_UNKNOWN");
        }

        return row.Payload.Deserialize<T>() ?? throw new InvalidDataException("PAGE_PRIVATE_STORE_INVALID");
    }

    private void Save(string id, string kind, object value)
    {
        var record = new PrivatePageRecord(
            1,
            new(id),
            kind,
            JsonSerializer.SerializeToElement(value)
        );
        privateStore.Save(
            id,
            JsonSerializer.SerializeToUtf8Bytes(record)
        );
        records[id] = record;
    }
}
