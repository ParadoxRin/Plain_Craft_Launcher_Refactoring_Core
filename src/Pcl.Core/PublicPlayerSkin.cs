using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Pcl.Core;
public sealed record PlayerSkinDownload(string PlayerName, string Uuid, string Model, byte[] Png);
/// <summary>只使用 Mojang 公开资料接口。每次调用均重新获取资料及 PNG，不发送账户或 OAuth 凭据。</summary>
public sealed class PublicPlayerSkin : IDisposable
{
    private readonly HttpClient http = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    // 依次查询玩家 UUID、公开纹理资料和官方 PNG；不需要微软账户令牌。
    public async Task<PlayerSkinDownload> FetchAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$"))
        {
            throw new InvalidDataException("正版玩家名只能包含 1–16 位英文字母、数字或下划线。");
        }

        var identity = JObject.Parse(
            Encoding.UTF8.GetString(
                await ReadAsync("https://api.mojang.com/users/profiles/minecraft/" + name, 65536, ct)
            )
        );
        var uuid = (string? )identity["id"] ?? "";
        if (!Regex.IsMatch(uuid, "^[a-fA-F0-9]{32}$"))
        {
            throw new InvalidDataException("玩家资料没有有效 UUID。");
        }

        var profile = JObject.Parse(
            Encoding.UTF8.GetString(
                await ReadAsync("https://sessionserver.mojang.com/session/minecraft/profile/" + uuid, 262144, ct)
            )
        );
        var encoded = (string? )profile["properties"]?.FirstOrDefault(
            x => (string? )x["name"] == "textures"
        )?["value"] ?? throw new InvalidDataException("该玩家没有公开皮肤。");
        var texture = JObject.Parse(
            Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded)
            )
        )["textures"]?["SKIN"] ?? throw new InvalidDataException("该玩家没有自定义皮肤。");
        var url = (string? )texture["url"] ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net" || uri.UserInfo.Length != 0 || !Regex.IsMatch(uri.AbsolutePath, "^/texture/[a-fA-F0-9]{32,128}$") || uri.Query.Length > 0 || uri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("皮肤地址不是官方纹理地址。");
        }

        var png = await ReadAsync("https://textures.minecraft.net" + uri.AbsolutePath, 4 * 1024 * 1024, ct);
        ExternalAuthentication.ValidatePng(png, "skin");
        return new(
            (string? )identity["name"] ?? name,
            uuid,
            (string? )texture["metadata"]?["model"] == "slim"
                ? "slim"
                : "wide",
            SkinPixels.Modern(png)
        );
    }

    // 每次刷新发起新请求，并在读取过程中限制响应大小。
    private async Task<byte[]> ReadAsync(string url, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new()
        {
            NoCache = true
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            throw new InvalidDataException("未找到该正版玩家或皮肤。");
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int size;
        while ((size = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + size > limit)
            {
                throw new InvalidDataException("皮肤响应超过大小限制。");
            }

            data.Write(buffer, 0, size);
        }

        return data.ToArray();
    }

    // 关闭窗口时释放 HTTP 连接池。
    public void Dispose() => http.Dispose();
}
