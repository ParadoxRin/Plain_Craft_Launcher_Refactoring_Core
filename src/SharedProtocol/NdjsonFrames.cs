using System.Runtime.CompilerServices;

namespace Pcl.Protocol;
internal sealed record ProtocolFrame(byte[]? Data, string? Error);
// 按字节限流，不能用无上限 ReadLineAsync；超长行丢弃至换行后继续同步下一帧。
internal static class NdjsonFrames
{
    public const int MaximumBytes = 1_048_576;
    public static async IAsyncEnumerable<ProtocolFrame> ReadAsync(Stream source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        bool oversized = false;
        int count;
        while ((count = await source.ReadAsync(buffer, ct)
            .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    if (oversized)
                    {
                        yield return new(null, "FRAME_TOO_LARGE");
                    }
                    else if (frame.Length > 0)
                    {
                        yield return new(
                            frame.ToArray(),
                            null
                        );
                    }

                    frame.SetLength(0);
                    oversized = false;
                }
                else if (!oversized)
                {
                    if (frame.Length == MaximumBytes)
                    {
                        frame.SetLength(0);
                        oversized = true;
                    }
                    else
                    {
                        frame.WriteByte(buffer[i]);
                    }
                }
            }
        }

        if (oversized)
        {
            yield return new(null, "FRAME_TOO_LARGE");
        }
        else if (frame.Length > 0)
        {
            yield return new(null, "INCOMPLETE_FRAME");
        }
    }
}
