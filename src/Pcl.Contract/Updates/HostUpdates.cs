namespace Pcl.Core.Contracts.Updates;

// 更新声明不包含账户、实例或凭据。DataEpoch 变化必须由客户端人工升级处理。
public sealed record HostBuild(string Version, long Sequence, int DataEpoch, string Runtime);
public sealed record HostUpdateEnvelope(string Payload, string Signature);
public sealed record HostUpdateFile(string Path, long Size, string Sha256, bool Executable);
public sealed record HostUpdateManifest(int Format, string Channel, HostBuild Build, int Protocol,
    string[] Capabilities, DateTimeOffset Expires, string PackageUrl, long PackageSize,
    string PackageSha256, string EntryPoint, bool FrameworkDependent, HostUpdateFile[] Files);
public sealed record HostUpdateStatus(string State, string Code, string? Version = null);
public sealed record HostUpdateReadiness(bool Ready, string Code);
