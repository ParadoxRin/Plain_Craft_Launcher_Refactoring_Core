using System.Buffers.Binary;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：MinecraftSkin 汇集 Id、State、Url、Variant，供该模块传递状态。
public sealed record MinecraftSkin(string Id, string State, string Url, string Variant)
{
    // [实现] ToString 的实现：计算并返回 (Variant == "SLIM" ? "细手臂" : "标准手臂") + (State == "ACTIVE" ? " · 当前" : "") + " · " + Id。
    public override string ToString() => (Variant == "SLIM"
        ? "细手臂"
        : "标准手臂") + (State == "ACTIVE"
        ? " · 当前"
        : "") + " · " + Id;
}

// [实现] 数据约定：MinecraftCape 汇集 Id、State、Url、Name，供该模块传递状态。
public sealed record MinecraftCape(string Id, string State, string Url, string Name)
{
    // [实现] ToString 的实现：计算并返回 Name + (State == "ACTIVE" ? " · 当前" : "")。
    public override string ToString() => Name + (State == "ACTIVE"
        ? " · 当前"
        : "");
}

// [实现] 数据约定：MinecraftAppearance 汇集 Id、Name、Skins、Capes，供该模块传递状态。
public sealed record MinecraftAppearance(
    string Id,
    string Name,
    IReadOnlyList<MinecraftSkin> Skins,
    IReadOnlyList<MinecraftCape> Capes
);
/// <summary>Uses the current Minecraft Services token obtained through the existing Microsoft entitlement-checked flow.</summary>
public sealed class MicrosoftSkinService : IDisposable
{
    private readonly HttpClient http;
    // [实现] MicrosoftSkinService 构造 的实现：计算并返回 http = new( handler ?? new HttpClientHandler { AllowAutoRedirect = false } ) { Timeout = TimeSp…。
    public MicrosoftSkinService(HttpMessageHandler? handler = null) => http = new(
        handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    // [实现] GetAsync 的实现：准备 var response = await SendAsync(HttpMethod.Get, "minecraft/profile", session, null, ct)；按 response.Content.Headers.ContentLength > 1024 * 1024 分支处理；准备 var input = await response.Content.ReadAsStreamAsync(ct)。
    public async Task<MinecraftAppearance> GetAsync(GameSession session, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "minecraft/profile", session, null, ct);
        if (response.Content.Headers.ContentLength > 1024 * 1024)
            throw new InvalidDataException("玩家档案响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            // [实现] 循环满足 (count = await input.ReadAsync(buffer, ct)) > 0 时：按 bytes.Length + count > 1024 * 1024 分支处理；执行 bytes.Write(buffer, 0, count)。
            if (bytes.Length + count > 1024 * 1024)
                throw new InvalidDataException("玩家档案响应过大。");
            bytes.Write(buffer, 0, count);
        }

        var json = JObject.Parse(
            Encoding.UTF8.GetString(
                bytes.ToArray()
            )
        );
        if (!string.Equals((string? )json["id"], session.Uuid, StringComparison.OrdinalIgnoreCase))
            throw new LoginRequiredException("皮肤档案与所选微软账户不一致，请重新登录。");
        return new(
            session.Uuid,
            (string? )json["name"] ?? session.Name,
            (json["skins"] as JArray ?? []).Select(
                x => new MinecraftSkin(
                    Id(x),
                    (string? )x["state"] ?? "INACTIVE",
                    TextureUrl((string? )x["url"] ?? ""),
                    (string? )x["variant"] ?? "CLASSIC"
                )
            )
                .ToArray(),
            (json["capes"] as JArray ?? []).Select(
                x => new MinecraftCape(
                    Id(x),
                    (string? )x["state"] ?? "INACTIVE",
                    TextureUrl((string? )x["url"] ?? ""),
                    (string? )x["alias"] ?? Id(x)
                )
            )
                .ToArray()
        );
    }

    // [实现] UploadSkinAsync 的实现：执行 ExternalAuthentication.ValidatePng(png, "skin")；按 BinaryPrimitives.ReadUInt32BigEndian( png.AsSpan(16, 4) ) != 64 || BinaryPrimitives.ReadUInt32B… 分支处理；准备 var body = new MultipartFormDataContent()。
    public async Task UploadSkinAsync(GameSession session, byte[] png, bool slim, CancellationToken ct = default)
    {
        ExternalAuthentication.ValidatePng(png, "skin");
        if (BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(16, 4)
        ) != 64 || BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(20, 4)
        )is not (32 or 64))
            throw new InvalidDataException("微软皮肤需为 64×64 或 64×32 PNG。");
        using var body = new MultipartFormDataContent();
        body.Add(
            new StringContent(
                slim
                    ? "slim"
                    : "classic"
            ),
            "variant"
        );
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new("image/png");
        body.Add(file, "file", "skin.png");
        using var response = await SendAsync(HttpMethod.Post, "minecraft/profile/skins", session, body, ct);
    }

    // [实现] UseSkinAsync 的实现：准备 var profile = await GetAsync(session, ct)；准备 var skin = profile.Skins.SingleOrDefault( x => x.Id == skinId ) ?? throw new InvalidDataExcepti…；准备 var body = Json( new JObject { ["url"] = skin.Url, ["variant"] = skin.Variant == "SLIM" ? "slim…。
    public async Task UseSkinAsync(GameSession session, string skinId, CancellationToken ct = default)
    {
        var profile = await GetAsync(session, ct);
        var skin = profile.Skins.SingleOrDefault(
            x => x.Id == skinId
        ) ?? throw new InvalidDataException("皮肤不在当前账户档案中。");
        using var body = Json(
            new JObject
            {
                ["url"] = skin.Url,
                ["variant"] = skin.Variant == "SLIM"
                    ? "slim"
                    : "classic"
            }
        );
        using var response = await SendAsync(HttpMethod.Post, "minecraft/profile/skins", session, body, ct);
    }

    // [实现] ResetSkinAsync 的实现：准备 var response = await SendAsync(HttpMethod.Delete, "minecraft/profile/skins/active", session, nu…。
    public async Task ResetSkinAsync(GameSession session, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, "minecraft/profile/skins/active", session, null, ct);
    }

    // [实现] UseCapeAsync 的实现：准备 var profile = await GetAsync(session, ct)；按 !profile.Capes.Any( x => x.Id == capeId ) 分支处理；准备 var body = Json( new JObject { ["capeId"] = capeId } )。
    public async Task UseCapeAsync(GameSession session, string capeId, CancellationToken ct = default)
    {
        var profile = await GetAsync(session, ct);
        if (!profile.Capes.Any(
            x => x.Id == capeId
        ))
            throw new InvalidOperationException("此账户没有该披风。");
        using var body = Json(
            new JObject
            {
                ["capeId"] = capeId
            }
        );
        using var response = await SendAsync(HttpMethod.Put, "minecraft/profile/capes/active", session, body, ct);
    }

    // [实现] HideCapeAsync 的实现：准备 var response = await SendAsync(HttpMethod.Delete, "minecraft/profile/capes/active", session, nu…。
    public async Task HideCapeAsync(GameSession session, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, "minecraft/profile/capes/active", session, null, ct);
    }

    // [实现] TextureUrl 的实现：按 !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net" ||… 分支处理；返回 new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri。
    public static string TextureUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != "textures.minecraft.net" || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.StartsWith("/texture/", StringComparison.Ordinal))
            throw new InvalidDataException("档案返回未知的皮肤服务地址。");
        return new UriBuilder(uri)
        {
            Scheme = "https",
            Port = -1
        }.Uri.AbsoluteUri;
    }

    // [实现] Id 的实现：准备 var id = (string? )value["id"] ?? ""；返回 Guid.TryParse(id, out _) ? id : throw new InvalidDataException("皮肤或披风标识无效。")。
    private static string Id(JToken value)
    {
        var id = (string? )value["id"] ?? "";
        return Guid.TryParse(id, out _)
            ? id
            : throw new InvalidDataException("皮肤或披风标识无效。");
    }

    // [实现] Json 的实现：计算并返回 new( json.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json" )。
    private static StringContent Json(JObject json) => new(
        json.ToString(Newtonsoft.Json.Formatting.None),
        Encoding.UTF8,
        "application/json"
    );
    // [实现] SendAsync 的实现：按 session.UserType != "msa" || session.Demo || session.AccessToken.Length < 10 || !Guid.TryParse(… 分支处理；准备 var request = new HttpRequestMessage(method, "https://api.minecraftservices.com/" + path) { Con…；执行 request.Headers.Authorization = new("Bearer", session.AccessToken)。
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        GameSession session,
        HttpContent? content,
        CancellationToken ct
    )
    {
        if (session.UserType != "msa" || session.Demo || session.AccessToken.Length < 10 || !Guid.TryParse(session.Uuid, out _))
            throw new LoginRequiredException("请先使用已拥有 Java 版的微软账户登录；需要此移植版合法 OAuth client ID。");
        using var request = new HttpRequestMessage(method, "https://api.minecraftservices.com/" + path)
        {
            Content = content
        };
        request.Headers.Authorization = new("Bearer", session.AccessToken);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
            return response;
        var code = response.StatusCode;
        response.Dispose();
        throw code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new LoginRequiredException("微软游戏授权已失效或无此操作权限，请重新登录。")
            : new HttpRequestException($"Minecraft 皮肤服务请求失败（{(int)code}）。", null, code);
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
