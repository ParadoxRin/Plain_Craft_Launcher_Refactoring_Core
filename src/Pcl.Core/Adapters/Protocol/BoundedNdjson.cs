using System.Runtime.CompilerServices;

namespace Pcl.Core.Adapters.Protocol;
public sealed record ProtocolFrame(byte[]? Data, string? Error);
// 旧核心公开入口保留；字节分帧算法与 Client 共用同一内部源文件，避免协议漂移。
public static class BoundedNdjson
{
    public const int MaximumBytes = Pcl.Protocol.NdjsonFrames.MaximumBytes;
    public static async IAsyncEnumerable<ProtocolFrame> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in Pcl.Protocol.NdjsonFrames.ReadAsync(source, ct)
            .ConfigureAwait(false))
        {
            yield return new(frame.Data, frame.Error);
        }
    }
}
