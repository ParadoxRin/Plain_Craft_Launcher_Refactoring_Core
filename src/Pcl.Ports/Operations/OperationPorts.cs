namespace Pcl.Core.Ports.Operations;
// 存储 Port 只处理版本化字节记录；业务状态和 Contract 映射由 Application 拥有。
public interface IOperationStore : IDisposable
{
    IReadOnlyList<byte[]> ReadAll();
    void Save(string operationId, byte[] document);
}

public sealed record OperationInstallInput(string GameVersion, string Loader, string Os, string Architecture, string OsVersion);
public sealed record OperationInstalledInstance(string Id, string Name, string GameVersion, string Loader, string GameDirectory);
public sealed record ReceiptTarget(string Os, string Architecture, string OsVersion);
public sealed record ReceiptFile(string Path, string Sha1, long Size);
public sealed record InstallReceipt(
    int FormatVersion,
    string OperationId,
    string InstanceId,
    string GameVersion,
    string Loader,
    ReceiptTarget Target,
    string MetadataSha1,
    string AssetIndexSha1,
    string PlanDigest,
    IReadOnlyList<ReceiptFile> Files,
    string CommitStage,
    LoaderRecoveryPin? LoaderPin = null
);
public interface IOperationInstaller
{
    Task<OperationInstalledInstance> InstallAsync(
        string operationId,
        OperationInstallInput request,
        InstallReceipt? previous,
        Action<InstallReceipt> checkpoint,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    );
}

// 技术执行拒绝只携带受控错误码；Application 决定如何映射公开失败。
public sealed class OperationInstallException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// 固定具体加载器、操作专属目标以及从可信源重新取得的安装器/元数据身份。
public sealed record LoaderRecoveryPin(
    string Family,
    string Version,
    string InstanceName,
    string OwnerOperationId,
    string ArtifactHash = "",
    string MetadataHash = "",
    string Stage = "selected",
    IReadOnlyList<ReceiptFile>? Files = null
);
