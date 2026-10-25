using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：DeviceChallenge 汇集 UserCode、VerificationUri、DeviceCode、Interval、ExpiresIn，供该模块传递状态。
public sealed record DeviceChallenge(string UserCode, string VerificationUri, string DeviceCode, int Interval, int ExpiresIn);
// [实现] 数据约定：MicrosoftLogin 汇集 Session、RefreshToken、ExpiresAt、ClientId，供该模块传递状态。
public sealed record MicrosoftLogin(GameSession Session, string? RefreshToken, DateTimeOffset ExpiresAt, string ClientId);
// 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。

// 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。
public sealed class MicrosoftAuthentication : IDisposable
{
    private readonly HttpClient client;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    // [实现] MicrosoftAuthentication 构造 的实现：执行 client = handler is null ? new() : new(handler)；执行 client.Timeout = TimeSpan.FromSeconds(45)；执行 this.delay = delay ?? Task.Delay。
    public MicrosoftAuthentication(
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null
    )
    {
        client = handler is null
            ? new()
            : new(handler);
        client.Timeout = TimeSpan.FromSeconds(45);
        this.delay = delay ?? Task.Delay;
    }

    // [实现] ClientId 的实现：计算并返回 Guid.TryParse( id.Trim(), out var guid ) && guid != Guid.Empty ? guid.ToString() : throw new In…。
    private static string ClientId(string id) => Guid.TryParse(
        id.Trim(),
        out var guid
    ) && guid != Guid.Empty
        ? guid.ToString()
        : throw new InvalidOperationException("此版本的微软应用配置无效，请更新应用后重试。");
    // [实现] BeginAsync 的实现：准备 var json = await FormAsync( "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode…；准备 var uri = new Uri((string)json["verification_uri"]!)；按 uri.Scheme != "https" || uri.Host is not ("microsoft.com" or "www.microsoft.com" or "login.micr… 分支处理。
    public async Task<DeviceChallenge> BeginAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var json = await FormAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            new()
            {
                ["client_id"] = ClientId(clientId),
                ["scope"] = "XboxLive.signin offline_access"
            },
            cancellationToken
        );
        var uri = new Uri((string)json["verification_uri"]!);
        if (uri.Scheme != "https" || uri.Host is not ("microsoft.com" or "www.microsoft.com" or "login.microsoftonline.com" or "login.live.com"))
            throw new InvalidDataException("登录服务返回了未知的验证网址。");
        return new(
            (string)json["user_code"]!,
            uri.AbsoluteUri,
            (string)json["device_code"]!,
            (int? )json["interval"] ?? 5,
            (int)json["expires_in"]!
        );
    }

    // [实现] CompleteAsync 的实现：计算并返回 (await CompleteLoginAsync(clientId, challenge, cancellationToken)).Session。
    public async Task<GameSession> CompleteAsync(string clientId, DeviceChallenge challenge, CancellationToken cancellationToken = default) => (await CompleteLoginAsync(clientId, challenge, cancellationToken)).Session;
    // [实现] CompleteLoginAsync 的实现：准备 var id = ClientId(clientId)；准备 var deadline = DateTimeOffset.UtcNow.AddSeconds( Math.Clamp(challenge.ExpiresIn, 0, 3600) )；准备 var interval = Math.Max(1, challenge.Interval)。
    public async Task<MicrosoftLogin> CompleteLoginAsync(string clientId, DeviceChallenge challenge, CancellationToken cancellationToken = default)
    {
        var id = ClientId(clientId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(challenge.ExpiresIn, 0, 3600)
        );
        var interval = Math.Max(1, challenge.Interval);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // [实现] 循环满足 DateTimeOffset.UtcNow < deadline 时：执行 await delay( TimeSpan.FromSeconds(interval), cancellationToken )；执行 cancellationToken.ThrowIfCancellationRequested()；按 DateTimeOffset.UtcNow >= deadline 分支处理。
            await delay(
                TimeSpan.FromSeconds(interval),
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
                break;
            var json = await FormAsync(
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                new()
                {
                    ["client_id"] = id,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = challenge.DeviceCode
                },
                cancellationToken,
                true
            );
            if (json["access_token"] is not null)
                return await ExchangeAsync(id, json, cancellationToken);
            var error = (string? )json["error"];
            if (error == "authorization_pending")
                continue;
            if (error == "slow_down")
            {
                // [实现] 条件成立（error == "slow_down"）时：执行 interval = Math.Min(interval + 5, 60)；执行 continue;。
                interval = Math.Min(interval + 5, 60);
                continue;
            }

            throw OAuthError(json);
        }

        throw new TimeoutException("微软设备码已过期，请重新登录。");
    }

    // [实现] RefreshAsync 的实现：准备 var id = ClientId(clientId)；按 string.IsNullOrWhiteSpace(refreshToken) 分支处理；准备 var json = await FormAsync( "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", ne…。
    public async Task<MicrosoftLogin> RefreshAsync(string clientId, string refreshToken, CancellationToken ct = default)
    {
        var id = ClientId(clientId);
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new LoginRequiredException("此微软账户需要重新登录。");
        var json = await FormAsync(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            new()
            {
                ["client_id"] = id,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = "XboxLive.signin offline_access"
            },
            ct
        );
        if (json["refresh_token"] is null)
            json["refresh_token"] = refreshToken;
        return await ExchangeAsync(id, json, ct);
    }

    // [实现] ExchangeAsync 的实现：准备 var xbl = await PostAsync( "https://user.auth.xboxlive.com/user/authenticate", new { Properties…；准备 var xsts = await PostAsync( "https://xsts.auth.xboxlive.com/xsts/authorize", new { Properties =…；准备 var uhs = (string)xsts["DisplayClaims"]!["xui"]![0]!["uhs"]!。
    private async Task<MicrosoftLogin> ExchangeAsync(string id, JObject oauth, CancellationToken ct)
    {
        var xbl = await PostAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + (string)oauth["access_token"]!
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            },
            ct
        );
        var xsts = await PostAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[]
                    {
                        (string)xbl["Token"]!
                    }
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT"
            },
            ct
        );
        var uhs = (string)xsts["DisplayClaims"]!["xui"]![0]!["uhs"]!;
        var minecraft = await PostAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new
            {
                identityToken = $"XBL3.0 x={uhs};{(string)xsts["Token"]!}"
            },
            ct
        );
        var token = (string? )minecraft["access_token"] ?? throw new InvalidDataException("游戏服务没有返回访问令牌。");
        var entitlements = await AuthorizedAsync("entitlements/mcstore", token, ct);
        if (!(entitlements["items"] ?? new JArray()).Any(
            x => (string? )x["name"] is "game_minecraft" or "product_minecraft"
        ))
            throw new InvalidOperationException("此微软账户没有可用的 Minecraft Java 版权益，请检查购买或订阅。");
        var profile = await AuthorizedAsync("minecraft/profile", token, ct);
        var uuid = (string? )profile["id"] ?? "";
        var name = (string? )profile["name"] ?? "";
        if (!Regex.IsMatch(uuid, "^[0-9a-fA-F]{32}$") || !Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$"))
            throw new InvalidDataException("Minecraft 档案无效，请先完成 Java 版玩家档案设置。");
        var expiry = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp((int? )minecraft["expires_in"] ?? 3600, 0, 86400)
        );
        var session = new GameSession(
            name,
            uuid.ToLowerInvariant(),
            token,
            "msa",
            false,
            (string? )xsts["DisplayClaims"]?["xui"]?[0]?["xid"]
        )
        {
            Ownership = new(
                name,
                uuid.ToLowerInvariant(),
                token,
                expiry
            )
        };
        return new(session, (string? )oauth["refresh_token"], expiry, id);
    }

    // [实现] AuthorizedAsync 的实现：准备 var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/" + pat…；执行 request.Headers.Authorization = new("Bearer", token)；准备 var response = await client.SendAsync(request, ct)。
    private async Task<JObject> AuthorizedAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/" + path);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new LoginRequiredException("游戏授权已失效，请重新登录微软账户。");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("无法取得 Minecraft 权益或档案，请确认该账户拥有 Java 版并已设置玩家名。");
        return JObject.Parse(
            await response.Content.ReadAsStringAsync(ct)
        );
    }

    // [实现] OAuthError 的实现：计算并返回 code switch { "authorization_declined" or "access_denied" => new InvalidOperationException("已取消…。
    private static Exception OAuthError(string? code) => code switch
    {
        "authorization_declined" or "access_denied" => new InvalidOperationException("已取消微软授权。"),
        "expired_token" => new LoginRequiredException("微软设备码已过期，请重新登录。"),
        "invalid_grant" or "interaction_required" => new LoginRequiredException("微软授权已失效，请重新登录。"),
        "invalid_client" or "unauthorized_client" => new InvalidOperationException("微软应用注册或公共客户端配置不可用，请联系应用维护者。"),
        _ => new InvalidOperationException("微软授权请求失败，请检查网络或稍后重试。")
    };
    /// <summary>保留微软数字错误码以定位刷新失败；不输出 error_description 中的身份、令牌或请求内容。</summary>
    private static Exception OAuthError(JObject response)
    {
        var failure = OAuthError((string? )response["error"]);
        var codes = (response["error_codes"] as JArray ?? new JArray()).Where(
            value => value.Type == JTokenType.Integer
        )
            .Select(
                value => value.ToString()
            )
            .Where(
                value => Regex.IsMatch(value, "^[0-9]{1,12}$")
            )
            .Take(4)
            .ToArray();
        if (codes.Length == 0)
        {
            // [实现] 条件成立（codes.Length == 0）时：执行 codes = Regex.Matches((string? )response["error_description"] ?? "", @"AADSTS([0-9]{1,12})") .S…。
            codes = Regex.Matches((string? )response["error_description"] ?? "", @"AADSTS([0-9]{1,12})")
                .Select(
                    match => match.Groups[1].Value
                )
                .Distinct()
                .Take(4)
                .ToArray();
        }

        if (codes.Length == 0)
        {
            // [实现] 条件成立（codes.Length == 0）时：返回 failure。
            return failure;
        }

        var message = failure.Message + "（微软错误码：" + string.Join(", ", codes) + "）";
        return failure is LoginRequiredException
            ? new LoginRequiredException(message)
            : new InvalidOperationException(message);
    }

    // [实现] FormAsync 的实现：执行 ct.ThrowIfCancellationRequested()；准备 var response = await client.PostAsync( url, new FormUrlEncodedContent(body), ct )；准备 var json = JObject.Parse( await response.Content.ReadAsStringAsync(ct) )。
    private async Task<JObject> FormAsync(
        string url,
        Dictionary<string, string> body,
        CancellationToken ct,
        bool allowError = false
    )
    {
        ct.ThrowIfCancellationRequested();
        using var response = await client.PostAsync(
            url,
            new FormUrlEncodedContent(body),
            ct
        );
        var json = JObject.Parse(
            await response.Content.ReadAsStringAsync(ct)
        );
        if (!allowError && (!response.IsSuccessStatusCode || json["error"] is not null))
            throw OAuthError(json);
        return json;
    }

    // [实现] PostAsync 的实现：准备 var content = new StringContent( JsonSerializer.Serialize(body), Encoding.UTF8, "application/js…；准备 var response = await client.PostAsync(url, content, ct)；按 !response.IsSuccessStatusCode 分支处理。
    private async Task<JObject> PostAsync(string url, object body, CancellationToken ct)
    {
        // Xbox's contract uses PascalCase. PostAsJsonAsync's web defaults silently camel-case it.
        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        );
        using var response = await client.PostAsync(url, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            // [实现] 条件成立（!response.IsSuccessStatusCode）时：按 new Uri(url).Host == "api.minecraftservices.com" && response.StatusCode == System.Net.HttpStatu… 分支处理；准备 var text = await response.Content.ReadAsStringAsync(ct)；准备 long? code = null。
            if (new Uri(url).Host == "api.minecraftservices.com" && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Minecraft 服务拒绝此应用（HTTP 403）。请联系应用维护者检查 API 审核和访问权限。");
            var text = await response.Content.ReadAsStringAsync(ct);
            long? code = null;
            try
            {
                // [实现] 在异常保护范围内：执行 code = (long? )JObject.Parse(text)["XErr"]。
                code = (long? )JObject.Parse(text)["XErr"];
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }

            throw new InvalidOperationException(
                code switch
                {
                    2148916233 => "此微软账户尚未建立 Xbox 档案，请先在 Xbox 网站完成设置。",
                    2148916238 => "此账户需要家庭组织者完成 Xbox 儿童账户权限设置。",
                    _ => $"登录服务拒绝请求：{new Uri(url).Host}（{(int)response.StatusCode}，Xbox 错误码：{code?.ToString() ?? "未提供"}）。请检查应用权限、账户状态和网络。"
                }
            );
        }

        return JObject.Parse(
            await response.Content.ReadAsStringAsync(ct)
        );
    }

    // [实现] Dispose 的实现：执行 client.Dispose()。
    public void Dispose() => client.Dispose();
}
