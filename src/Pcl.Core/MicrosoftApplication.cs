using System.Text.Json;

namespace Pcl.Core;
/// <summary>Public application identity, embedded at build time. Never contains client secrets or user tokens.</summary>
public sealed record MicrosoftApplication(string ClientId)
{
    public static MicrosoftApplication BuiltIn { get; } = LoadBuiltIn();
    public bool IsConfigured => Guid.TryParse(ClientId, out var id) && id != Guid.Empty;

    // Reject unconfigured builds before any authorization request; never fall back to the original application identity.
    public string RequireClientId() => IsConfigured
        ? Guid.Parse(ClientId)
            .ToString()
        : throw new InvalidOperationException("此构建未配置微软应用 client ID。开发者需配置自己的应用后重新构建，再使用自己的微软账号登录。下载和离线账户仍可使用。");
    // [实现] Parse 的实现：准备 var document = JsonDocument.Parse(json)；准备 var root = document.RootElement；按 root.ValueKind != JsonValueKind.Object || root.EnumerateObject() .Any( x => x.Name != "clientId… 分支处理。
    public static MicrosoftApplication Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject()
            .Any(
                x => x.Name != "clientId"
            ) || !root.TryGetProperty("clientId", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("微软应用配置只允许公开的 clientId 字段。");
        var id = value.GetString()!.Trim();
        if (id.Length == 0)
            return new("");
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
            throw new InvalidDataException("微软应用 clientId 无效。");
        return new(
            guid.ToString()
        );
    }

    // [实现] LoadBuiltIn 的实现：准备 var stream = typeof(MicrosoftApplication).Assembly.GetManifestResourceStream("Pcl.Core.Authenti…；准备 var reader = new StreamReader(stream)；返回 Parse( reader.ReadToEnd() )。
    private static MicrosoftApplication LoadBuiltIn()
    {
        using var stream = typeof(MicrosoftApplication).Assembly.GetManifestResourceStream("Pcl.Core.Authentication.microsoft-application.json") ?? throw new InvalidDataException("缺少内置微软应用配置。");
        using var reader = new StreamReader(stream);
        return Parse(
            reader.ReadToEnd()
        );
    }
}
