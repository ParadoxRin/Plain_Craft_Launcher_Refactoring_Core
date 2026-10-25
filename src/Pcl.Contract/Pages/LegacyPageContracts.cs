using Pcl.Core.Contracts.Installation;

namespace Pcl.Core.Contracts.Pages;
/// <summary>页面只传递显示数据与不透明选择标识，安装计划和下载地址留在适配器内部。</summary>
public sealed record ComponentSource(string Id, string Name, string GameVersion, string Loader);
public sealed record ComponentPreview(
    string PlanId,
    string TargetLoader,
    string? TargetVersion,
    bool DisableMods,
    int FileCount,
    string[] ActiveMods,
    string[] Conflicts
)
{
    public string SourceDirectory { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
    public bool SuppressConfirmation { get; init; }
    public bool CanApply => Conflicts.Length == 0;
}

public sealed record ComponentResult(string ProfileId, string ProfileName, string Backup, string Receipt, int DisabledMods)
{
    public string SourceDirectory { get; init; } = "";
    public string TargetDirectory { get; init; } = "";
}

public interface IComponentPageService
{
    Task SetConfirmationSuppressedAsync(bool suppressed, CancellationToken ct);
    Task<IReadOnlyList<string>> VersionsAsync(string loader, CancellationToken ct);
    Task<ComponentPreview> PreviewAsync(string loader, string? version, bool disable, CancellationToken ct);
    Task<ComponentResult> ApplyAsync(string planId, IProgress<VersionInstallProgress>? progress, CancellationToken ct);
}

public sealed record ArtifactChoice(string SelectionId, string Version, string FileName)
{
    public override string ToString() => Version + " · " + FileName;
}

public sealed record ArtifactSaved(string Path, string Verification);
public sealed record ArtifactNotes(string Title, string Text);
public interface IArtifactPageService
{
    Task<IReadOnlyList<ArtifactChoice>> ListAsync(string version, string kind, CancellationToken ct);
    Task<ArtifactSaved> SaveAsync(string selectionId, string destination, CancellationToken ct);
    Task<ArtifactNotes?> NotesAsync(string version, CancellationToken ct);
}
