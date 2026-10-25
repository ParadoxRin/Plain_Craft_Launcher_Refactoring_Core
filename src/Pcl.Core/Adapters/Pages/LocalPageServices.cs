using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Core.Adapters.Pages;
// 仅供显式 --in-process 兼容模式；默认桌面通过 Host 执行，Shell 内不保留安装算法。
public sealed class LocalComponentPageService(MinecraftService source, GameProfile profile) : IComponentPageService
{
    private readonly InstanceComponentService components = new(source);
    private readonly Dictionary<string, ComponentMigrationPlan> plans = new();
    public Task SetConfirmationSuppressedAsync(bool suppressed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ComponentConfirmationPreference.Save(source.Paths.Root, suppressed);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> VersionsAsync(string loader, CancellationToken ct) => components.VersionsAsync(profile, loader, ct);
    public async Task<ComponentPreview> PreviewAsync(string loader, string? version, bool disable, CancellationToken ct)
    {
        var plan = await Task.Run(
            () => components.Preview(profile, loader, version, disable, ct),
            ct
        );
        Directory.CreateDirectory(
            ResourceInstaller.SafeTarget(source.Paths.Root, "instances")
        );
        plans.Clear();
        var id = Guid.NewGuid()
            .ToString("N");
        plan = plan with
        {
            TargetKey = id
        };
        plans.Add(id, plan);
        return new(
            id,
            plan.TargetLoader,
            plan.TargetVersion,
            plan.DisableMods,
            plan.Files.Count,
            plan.ActiveMods.ToArray(),
            plan.Conflicts.ToArray()
        )
        {
            SourceDirectory = profile.GameDirectory,
            TargetDirectory = Path.Combine(source.Paths.Root, "instances", "modified-" + id),
            SuppressConfirmation = ComponentConfirmationPreference.Read(source.Paths.Root)
        };
    }

    public async Task<ComponentResult> ApplyAsync(string planId, IProgress<VersionInstallProgress>? progress, CancellationToken ct)
    {
        if (!plans.TryGetValue(planId, out var plan))
        {
            throw new InvalidOperationException("变更预览已失效，请重新预览。");
        }

        // 原流程再次校验文件指纹；取消与失败不消费计划，成功后不允许重复创建副本。
        var result = await components.ApplyAsync(
            plan,
            new Progress<InstallProgress>(
                x => progress?.Report(
                    new(x.Stage, x.Completed, x.Total, x.Bytes, x.TotalBytes)
                )
            ),
            ct
        );
        plans.Remove(planId);
        return new(
            result.Profile.Id,
            result.Profile.Name,
            result.Backup,
            result.Receipt,
            result.DisabledMods
        )
        {
            SourceDirectory = profile.GameDirectory,
            TargetDirectory = result.Profile.GameDirectory
        };
    }
}

public sealed class LocalArtifactPageService(MinecraftService source) : IArtifactPageService
{
    private readonly Dictionary<string, (string Version, DownloadArtifact File)> selections = new();
    public async Task<IReadOnlyList<ArtifactChoice>> ListAsync(string version, string kind, CancellationToken ct)
    {
        using var target = source.ForVersion(version);
        using var downloads = new ArtifactDownloadService(target);
        var files = await downloads.ListAsync(kind, ct);
        selections.Clear();
        return files.Select(
            file =>
            {
                var id = Guid.NewGuid()
                    .ToString("N");
                selections.Add(id, (version, file));
                return new ArtifactChoice(id, file.Version, file.FileName);
            }
        )
            .ToArray();
    }

    public async Task<ArtifactSaved> SaveAsync(string selectionId, string destination, CancellationToken ct)
    {
        if (!selections.TryGetValue(selectionId, out var selected))
        {
            throw new InvalidOperationException("文件选择已失效，请重新读取列表。");
        }

        using var target = source.ForVersion(selected.Version);
        using var downloads = new ArtifactDownloadService(target);
        var saved = await downloads.SaveAsync(selected.File, destination, ct);
        return new(saved.Path, saved.Verification);
    }

    public async Task<ArtifactNotes?> NotesAsync(string version, CancellationToken ct)
    {
        using var target = source.ForVersion(version);
        using var downloads = new ArtifactDownloadService(target);
        var result = await downloads.NotesAsync(ct);
        return result is null
            ? null
            : new(result.Title, result.Text);
    }
}
