using System.Globalization;

namespace Pcl.Core.Adapters.Protocol;
/// <summary>新客户端使用连接随机前缀与递增序号，常数内存拒绝重放；旧请求格式保留有界兼容。</summary>
public sealed class RequestIdentityTracker
{
    private readonly HashSet<string> legacy = new(StringComparer.Ordinal);
    private string? prefix;
    private long last;
    private bool legacyMode;
    public bool Accept(string id)
    {
        int separator = id.IndexOf(':');
        if (separator == 32 && Guid.TryParseExact(id[..separator], "N", out _) && long.TryParse(
            id.AsSpan(separator + 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var sequence
        ))
        {
            if (legacyMode || sequence <= 0 || last == long.MaxValue || sequence != last + 1)
            {
                return false;
            }

            var candidate = id[..separator];
            if (prefix is not null && prefix != candidate)
            {
                return false;
            }

            prefix = candidate;
            last = sequence;
            return true;
        }

        if (prefix is not null)
        {
            return false;
        }

        legacyMode = true;
        return legacy.Count < 65536 && legacy.Add(id);
    }
}
