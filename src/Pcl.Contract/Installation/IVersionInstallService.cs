namespace Pcl.Core.Contracts.Installation;
/// <summary>阶段一同进程契约：等待安装完成；取消令牌控制本次安装，不承诺后台受理或重连恢复。</summary>
public interface IVersionInstallService
{
    Pcl.Core.Contracts.Operations.IVersionInstallOperations? Operations => null;

    Task<VersionInstallResult> InstallAsync(
        VersionInstallRequest request,
        IProgress<VersionInstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>显式安装目标，避免把宿主探测或核心服务类型传入界面契约。</summary>
public sealed record InstallationTarget(string Os, string Architecture, string OsVersion);
public sealed record VersionInstallRequest(string GameVersion, string Loader, InstallationTarget Target);
public sealed record VersionInstallProgress(string Stage, int Completed, int Total, long Bytes, long TotalBytes);
public sealed record InstalledInstance(string Id, string Name, string GameVersion, string Loader, string GameDirectory);
public sealed record InstallationError(string Code);
public sealed record VersionInstallResult(InstalledInstance? Instance, InstallationError? Error)
{
    public const int ContractVersion = 1;
}
