namespace Pcl.Core.Contracts.Host;
public sealed record HostHello(
    int ProtocolVersion,
    string HostInstanceId,
    string[] Capabilities,
    int MaximumFrameBytes,
    Pcl.Core.Contracts.Installation.InstallationTarget? InstallationTarget = null
);
public sealed record ResourceReference(string ResourceId, long Size, string Mime, string Sha256, int? Count = null);
public sealed record ResourceChunk(long Offset, string Base64, bool Eof);
public sealed record ResourcePage(int Offset, System.Text.Json.JsonElement[] Items, bool Eof);
public sealed record OperationLogEntry(string OperationId, long Sequence, string Level, string Code, bool Truncated = false);
