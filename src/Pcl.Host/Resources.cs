using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Host;
// 资源句柄只引用 Host 已选定的数据快照，不接受客户端路径；离线/关闭连接时整体回收。
internal sealed class Resources
{
    private sealed record Entry(
        byte[] Bytes,
        string Mime,
        DateTimeOffset Expires,
        JsonElement[]? Items,
        FileStream? File = null
    )
    {
        public long Length => File?.Length ?? Bytes.LongLength;
    }

    private readonly Dictionary<string, Entry> entries = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    internal ResourceReference Open(byte[] bytes, string mime, JsonElement[]? items = null)
    {
        foreach (var expired in entries.Where(
            x => x.Value.Expires < DateTimeOffset.UtcNow
        )
            .Select(
                x => x.Key
            )
            .ToArray())
        {
            Close(expired);
        }

        if (bytes.Length > 16 * 1024 * 1024 || entries.Count >= 16 || entries.Values.Sum(
            x => (long)x.Bytes.Length
        ) + bytes.Length > 32 * 1024 * 1024)
        {
            throw new OperationRejectedException("RESOURCE_LIMIT");
        }

        var id = "res-" + Guid.NewGuid()
            .ToString("N");
        entries.Add(
            id,
            new(
                bytes,
                mime,
                DateTimeOffset.UtcNow.AddMinutes(5),
                items
            )
        );
        return new(
            id,
            bytes.Length,
            mime,
            Convert.ToHexString(
                SHA256.HashData(bytes)
            ),
            items?.Length
        );
    }

    // 文件由 Host 执行器选定，使用只读句柄与分块传输，不将整个 JAR 放进内存或单帧响应。
    internal ResourceReference OpenFile(string path, long size, string sha256)
    {
        foreach (var expired in entries.Where(
            x => x.Value.Expires < DateTimeOffset.UtcNow
        )
            .Select(
                x => x.Key
            )
            .ToArray())
        {
            Close(expired);
        }

        if (entries.Count >= 16 || size < 0 || size > 2L * 1024 * 1024 * 1024)
        {
            throw new OperationRejectedException("RESOURCE_LIMIT");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != size)
        {
            stream.Dispose();
            throw new OperationRejectedException("EXPORT_INTEGRITY_FAILED");
        }

        // 资源类型属于文件传输契约，图标和备份不能冒充 JAR。
        var mime = Path.GetExtension(path)
            .ToLowerInvariant() switch
        {
            ".jar" => "application/java-archive",
            ".png" => "image/png",
            ".zip" or ".mrpack" => "application/zip",
            _ => "application/octet-stream"
        };
        var id = "res-" + Guid.NewGuid()
            .ToString("N");
        entries.Add(
            id,
            new(
                [],
                mime,
                DateTimeOffset.UtcNow.AddMinutes(5),
                null,
                stream
            )
        );
        return new(id, size, mime, sha256);
    }

    internal ResourceReference OpenItems(object[] values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values, Json);
        using var document = JsonDocument.Parse(bytes);
        return Open(
            bytes,
            "application/json",
            document.RootElement.EnumerateArray()
                .Select(
                    x => x.Clone()
                )
                .ToArray()
        );
    }

    internal ResourceChunk Read(string id, long offset, int length)
    {
        var resource = Find(id);
        if (offset < 0 || offset > resource.Length || length is < 1 or > 262144)
        {
            throw new OperationRejectedException("RESOURCE_RANGE_INVALID");
        }

        int actual = (int)Math.Min(length, resource.Length - offset);
        var bytes = new byte[actual];
        if (resource.File is { } file)
        {
            file.Position = offset;
            file.ReadExactly(bytes);
        }
        else
        {
            Array.Copy(resource.Bytes, offset, bytes, 0, actual);
        }

        return new(
            offset,
            Convert.ToBase64String(bytes),
            offset + actual == resource.Length
        );
    }

    internal ResourcePage Page(string id, int offset, int count)
    {
        var items = Find(id)
            .Items ?? throw new OperationRejectedException("RESOURCE_NOT_PAGEABLE");
        if (offset < 0 || offset > items.Length || count is < 1 or > 50)
        {
            throw new OperationRejectedException("RESOURCE_RANGE_INVALID");
        }

        var page = items.Skip(offset)
            .Take(count)
            .ToArray();
        return new(offset, page, offset + page.Length == items.Length);
    }

    internal void Close(string id)
    {
        if (entries.Remove(id, out var entry))
        {
            entry.File?.Dispose();
        }
    }

    internal void Clear()
    {
        foreach (var id in entries.Keys.ToArray())
        {
            Close(id);
        }
    }

    private Entry Find(string id)
    {
        if (!entries.TryGetValue(id, out var entry) || entry.Expires < DateTimeOffset.UtcNow)
        {
            Close(id);
            throw new OperationRejectedException("RESOURCE_EXPIRED_OR_UNKNOWN");
        }

        return entry;
    }
}
