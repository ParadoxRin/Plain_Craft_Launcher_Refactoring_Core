using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：ExternalServer 汇集 ApiRoot、Name、Metadata，供该模块传递状态。
public sealed record ExternalServer(string ApiRoot, string Name, string Metadata);
// [实现] 数据约定：ExternalProfile 汇集 Id、Name，供该模块传递状态。
public sealed record ExternalProfile(string Id, string Name)
{
    // [实现] ToString 的实现：计算并返回 Name。
    public override string ToString() => Name;
}

// [实现] 数据约定：ExternalCredential 汇集 Session、ClientToken，供该模块传递状态。
public sealed record ExternalCredential(GameSession Session, string ClientToken);
// [实现] 数据约定：ExternalLogin 汇集 Server、Identifier、Credential、Profiles，供该模块传递状态。
public sealed record ExternalLogin(
    ExternalServer Server,
    string Identifier,
    ExternalCredential Credential,
    IReadOnlyList<ExternalProfile> Profiles
);
// [实现] 数据约定：ExternalLoginChallenge 汇集 Server、Identifier、AccessToken、ClientToken、Profiles、Selected，供该模块传递状态。
public sealed record ExternalLoginChallenge(
    ExternalServer Server,
    string Identifier,
    string AccessToken,
    string ClientToken,
    IReadOnlyList<ExternalProfile> Profiles,
    ExternalProfile? Selected
);
// [实现] 数据约定：ExternalTextures 汇集 SkinUrl、CapeUrl、Model、Uploadable，供该模块传递状态。
public sealed record ExternalTextures(string? SkinUrl, string? CapeUrl, string Model, IReadOnlyList<string> Uploadable);
// 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。
public sealed class ExternalAuthentication : IDisposable
{
    public const string AgentMetadataUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private readonly HttpClient http;
    // [实现] ExternalAuthentication 构造 的实现：执行 http = new( handler ?? new HttpClientHandler { AllowAutoRedirect = false } )；执行 http.Timeout = TimeSpan.FromSeconds(40)；执行 http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.8")。
    public ExternalAuthentication(HttpMessageHandler? handler = null)
    {
        http = new(
            handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false
            }
        );
        http.Timeout = TimeSpan.FromSeconds(40);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.8");
    }

    // [实现] Https 的实现：执行 address = address.Trim()；准备 string prefix = "authlib-injector:yggdrasil-server:"；按 address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) 分支处理。
    public static Uri Https(string address)
    {
        address = address.Trim();
        const string prefix = "authlib-injector:yggdrasil-server:";
        if (address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            address = Uri.UnescapeDataString(address[prefix.Length..]);
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0)
            throw new InvalidDataException("外置认证需要不含账号、查询参数或片段的 HTTPS 地址。");
        return uri;
    }

    // [实现] Identity 的实现：准备 var canonical = Https(server) .AbsoluteUri.TrimEnd('/') + "/"；执行 ValidateProfile( new(uuid, "Player") )；按 string.IsNullOrWhiteSpace(identifier) || identifier.Length > 512 || identifier.Any(char.IsContr… 分支处理。
    public static string Identity(string server, string identifier, string uuid)
    {
        var canonical = Https(server)
            .AbsoluteUri.TrimEnd('/') + "/";
        ValidateProfile(
            new(uuid, "Player")
        );
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 512 || identifier.Any(char.IsControl))
            throw new InvalidDataException("外置账号标识无效。");
        return "external-" + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    canonical + "\n" + identifier + "\n" + uuid.ToLowerInvariant()
                )
            )
        )
            .ToLowerInvariant();
    }

    // [实现] DiscoverAsync 的实现：准备 var uri = Https(input)；在 redirects < 8 条件下迭代；抛出 new InvalidDataException("认证服务器重定向次数过多。")。
    public async Task<ExternalServer> DiscoverAsync(string input, CancellationToken ct = default)
    {
        var uri = Https(input);
        for (int redirects = 0; redirects < 8; redirects++)
        {
            // [实现] 每次迭代（redirects < 8）：准备 var request = new HttpRequestMessage(HttpMethod.Get, uri)；准备 var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)；按 (int)response.StatusCode is >= 300 and < 400 分支处理。
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                // [实现] 条件成立（(int)response.StatusCode is >= 300 and < 400）时：执行 uri = Https( new Uri( uri, response.Headers.Location ?? throw new InvalidDataException("认证跳转缺少地…；执行 continue;。
                uri = Https(
                    new Uri(
                        uri,
                        response.Headers.Location ?? throw new InvalidDataException("认证跳转缺少地址。")
                    ).AbsoluteUri
                );
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var headers))
            {
                // [实现] 条件成立（response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var headers)）时：准备 var destination = Https( new Uri( uri, headers.First() ).AbsoluteUri )；按 destination != uri 分支处理。
                var destination = Https(
                    new Uri(
                        uri,
                        headers.First()
                    ).AbsoluteUri
                );
                if (destination != uri)
                {
                    // [实现] 条件成立（destination != uri）时：执行 uri = destination；执行 continue;。
                    uri = destination;
                    continue;
                }
            }

            var metadata = await ReadTextAsync(response, ct);
            var json = JObject.Parse(metadata);
            if (json["signaturePublickey"] is not JValue || json["skinDomains"] is not JArray)
                throw new InvalidDataException("该地址没有返回有效的 authlib-injector 元数据。");
            if (Encoding.UTF8.GetByteCount(metadata) > 65536)
                throw new InvalidDataException("认证服务器元数据过大，无法安全传入游戏。");
            try
            {
                // [实现] 在异常保护范围内：准备 var publicKey = RSA.Create()；执行 publicKey.ImportFromPem((string)json["signaturePublickey"]!)。
                using var publicKey = RSA.Create();
                publicKey.ImportFromPem((string)json["signaturePublickey"]!);
            }
            catch (Exception ex)when (ex is ArgumentException or CryptographicException)
            {
                // [实现] 捕获 (Exception ex) 后：抛出 new InvalidDataException("认证服务器签名公钥无效。")。
                throw new InvalidDataException("认证服务器签名公钥无效。");
            }

            var name = (string? )json["meta"]?["serverName"] ?? uri.Host;
            if (name.Length > 200)
                name = name[..200];
            return new(
                uri.AbsoluteUri.TrimEnd('/') + "/",
                name,
                metadata
            );
        }

        throw new InvalidDataException("认证服务器重定向次数过多。");
    }

    // [实现] AuthenticateAsync 的实现：执行 _ = Identity( server.ApiRoot, identifier, new string ('0', 32) )；按 string.IsNullOrEmpty(password) || password.Length > 4096 分支处理；准备 var clientToken = Guid.NewGuid() .ToString("N")。
    public async Task<ExternalLoginChallenge> AuthenticateAsync(ExternalServer server, string identifier, string password, CancellationToken ct = default)
    {
        _ = Identity(
            server.ApiRoot,
            identifier,
            new string ('0', 32)
        );
        if (string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new InvalidDataException("请填写密码。");
        var clientToken = Guid.NewGuid()
            .ToString("N");
        var json = await PostAsync(
            server,
            "authserver/authenticate",
            new
            {
                username = identifier,
                password,
                clientToken,
                requestUser = true,
                agent = new
                {
                    name = "Minecraft",
                    version = 1
                }
            },
            ct
        );
        var access = Token(json, "accessToken");
        var actualClient = Token(json, "clientToken");
        if (actualClient != clientToken)
            throw new InvalidDataException("认证服务器返回的客户端令牌不一致。");
        var available = (json["availableProfiles"] as JArray ?? []).Select(ReadProfile)
            .ToArray();
        var selected = json["selectedProfile"] is JObject profile
            ? ReadProfile(profile)
            : null;
        if (selected is not null && !available.Any(
            x => x.Id == selected.Id
        ))
            available = available.Append(selected)
                .ToArray();
        if (available.Length == 0)
            throw new LoginRequiredException("此账号没有 Minecraft 角色，请先到认证服务器创建角色。");
        return new(server, identifier, access, clientToken, available, selected);
    }

    // [实现] SelectProfileAsync 的实现：准备 var profile = challenge.Profiles.SingleOrDefault( x => x.Id == profileId ) ?? throw new Invalid…；按 challenge.Selected is not null 分支处理；准备 var json = await PostAsync( challenge.Server, "authserver/refresh", new { accessToken = challen…。
    public async Task<ExternalLogin> SelectProfileAsync(ExternalLoginChallenge challenge, string profileId, CancellationToken ct = default)
    {
        var profile = challenge.Profiles.SingleOrDefault(
            x => x.Id == profileId
        ) ?? throw new InvalidDataException("角色不属于此登录结果。");
        if (challenge.Selected is not null)
        {
            // [实现] 条件成立（challenge.Selected is not null）时：按 profile.Id != challenge.Selected.Id 分支处理；返回 Login( challenge.Server, challenge.Identifier, challenge.AccessToken, challenge.ClientToken, pr…。
            if (profile.Id != challenge.Selected.Id)
                throw new InvalidDataException("令牌已绑定其他角色，请重新登录选择。");
            return Login(
                challenge.Server,
                challenge.Identifier,
                challenge.AccessToken,
                challenge.ClientToken,
                profile,
                challenge.Profiles
            );
        }

        var json = await PostAsync(
            challenge.Server,
            "authserver/refresh",
            new
            {
                accessToken = challenge.AccessToken,
                clientToken = challenge.ClientToken,
                requestUser = true,
                selectedProfile = new
                {
                    id = profile.Id,
                    name = profile.Name
                }
            },
            ct
        );
        var selected = ReadProfile(
            json["selectedProfile"] ?? throw new InvalidDataException("未返回所选角色。")
        );
        if (selected.Id != profile.Id || Token(json, "clientToken") != challenge.ClientToken)
            throw new InvalidDataException("绑定返回的角色或客户端令牌不一致。");
        return Login(
            challenge.Server,
            challenge.Identifier,
            Token(json, "accessToken"),
            challenge.ClientToken,
            selected,
            challenge.Profiles
        );
    }

    // [实现] ValidateOrRefreshAsync 的实现：准备 var body = new { accessToken = credential.Session.AccessToken, clientToken = credential.ClientT…；准备 var request = Request(HttpMethod.Post, server, "authserver/validate", body)；准备 var response = await http.SendAsync(request, ct)。
    public async Task<ExternalLogin> ValidateOrRefreshAsync(
        ExternalServer server,
        string identifier,
        ExternalCredential credential,
        CancellationToken ct = default
    )
    {
        var body = new
        {
            accessToken = credential.Session.AccessToken,
            clientToken = credential.ClientToken
        };
        using var request = Request(HttpMethod.Post, server, "authserver/validate", body);
        using var response = await http.SendAsync(request, ct);
        var profile = new ExternalProfile(credential.Session.Uuid, credential.Session.Name);
        ValidateProfile(profile);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return Login(
                server,
                identifier,
                credential.Session.AccessToken,
                credential.ClientToken,
                profile,
                [
                    profile
                ]
            );
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
            throw ResponseError(response.StatusCode);
        var json = await PostAsync(
            server,
            "authserver/refresh",
            new
            {
                body.accessToken,
                body.clientToken,
                requestUser = true
            },
            ct
        );
        var refreshed = ReadProfile(
            json["selectedProfile"] ?? throw new LoginRequiredException("外置账户已失去角色绑定，请重新登录。")
        );
        if (refreshed.Id != profile.Id || Token(json, "clientToken") != credential.ClientToken)
            throw new LoginRequiredException("刷新后身份不一致，请重新登录。");
        return Login(
            server,
            identifier,
            Token(json, "accessToken"),
            credential.ClientToken,
            refreshed,
            [
                refreshed
            ]
        );
    }

    // [实现] InvalidateAsync 的实现：准备 var request = Request( HttpMethod.Post, server, "authserver/invalidate", new { accessToken = cr…；准备 var response = await http.SendAsync(request, ct)；按 response.StatusCode != HttpStatusCode.NoContent 分支处理。
    public async Task InvalidateAsync(ExternalServer server, ExternalCredential credential, CancellationToken ct = default)
    {
        using var request = Request(
            HttpMethod.Post,
            server,
            "authserver/invalidate",
            new
            {
                accessToken = credential.Session.AccessToken,
                clientToken = credential.ClientToken
            }
        );
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw ResponseError(response.StatusCode);
    }

    // [实现] LaunchArgumentsAsync 的实现：准备 var fresh = await DiscoverAsync(server.ApiRoot, ct)；按 fresh.ApiRoot != server.ApiRoot 分支处理；准备 var response = await http.GetAsync(AgentMetadataUrl, ct)。
    public async Task<IReadOnlyList<string>> LaunchArgumentsAsync(ExternalServer server, MinecraftService service, CancellationToken ct = default)
    {
        // Fetch public metadata afresh. Authentication credentials are never sent to the agent download service.
        var fresh = await DiscoverAsync(server.ApiRoot, ct);
        if (fresh.ApiRoot != server.ApiRoot)
            throw new InvalidDataException("认证服务器地址已改变，请重新确认并登录。");
        using var response = await http.GetAsync(AgentMetadataUrl, ct);
        response.EnsureSuccessStatusCode();
        var metadata = JObject.Parse(
            await ReadTextAsync(response, ct)
        );
        var hash = (string? )metadata["checksums"]?["sha256"] ?? "";
        var download = Https((string? )metadata["download_url"] ?? "");
        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || download.Host != "authlib-injector.yushi.moe")
            throw new InvalidDataException("authlib-injector 下载元数据无效。");
        var path = Path.Combine(
            service.Paths.Root,
            "authlib-injector",
            hash.ToLowerInvariant() + ".jar"
        );
        await service.Downloads.DownloadAsync(
            new(download, path, -1, hash),
            ct
        );
        return[
            "-javaagent:" + path + "=" + server.ApiRoot,
            "-Dauthlibinjector.yggdrasil.prefetched=" + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(fresh.Metadata)
            ),
            "-Dauthlibinjector.noLogFile"
        ];
    }

    // [实现] AddJvmArguments 的实现：计算并返回 plan with { Arguments = arguments.Concat(plan.Arguments) .ToArray() }。
    public static LaunchPlan AddJvmArguments(LaunchPlan plan, IReadOnlyList<string> arguments) => plan with
    {
        Arguments = arguments.Concat(plan.Arguments)
            .ToArray()
    };
    // [实现] TexturesAsync 的实现：执行 ValidateProfile( new(uuid, "Player") )；准备 var request = Request( HttpMethod.Get, server, "sessionserver/session/minecraft/profile/" + uui…；准备 var response = await http.SendAsync(request, ct)。
    public async Task<ExternalTextures> TexturesAsync(ExternalServer server, string uuid, CancellationToken ct = default)
    {
        ValidateProfile(
            new(uuid, "Player")
        );
        using var request = Request(
            HttpMethod.Get,
            server,
            "sessionserver/session/minecraft/profile/" + uuid + "?unsigned=true"
        );
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw ResponseError(response.StatusCode);
        var json = JObject.Parse(
            await ReadTextAsync(response, ct)
        );
        if (ReadProfile(json)
            .Id != uuid)
            throw new InvalidDataException("皮肤档案身份不匹配。");
        var properties = json["properties"] as JArray ?? [];
        var texture = (string? )properties.FirstOrDefault(
            x => (string? )x["name"] == "textures"
        )?["value"];
        var uploadable = ((string? )properties.FirstOrDefault(
            x => (string? )x["name"] == "uploadableTextures"
        )?["value"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(
                x => x is "skin" or "cape"
            )
            .ToArray();
        if (texture is null)
            return new(null, null, "default", uploadable);
        if (texture.Length > 65536)
            throw new InvalidDataException("皮肤属性过大。");
        var data = JObject.Parse(
            Encoding.UTF8.GetString(
                Convert.FromBase64String(texture)
            )
        );
        if ((string? )data["profileId"] is string actual && actual != uuid)
            throw new InvalidDataException("皮肤属性身份不匹配。");
        // [实现] Url 的实现：准备 var value = (string? )data["textures"]?[kind]?["url"]；按 value is null 分支处理；准备 var uri = Https(value)。
        string? Url(string kind)
        {
            var value = (string? )data["textures"]?[kind]?["url"];
            if (value is null)
                return null;
            var uri = Https(value);
            var rules = (JArray? )JObject.Parse(server.Metadata)["skinDomains"] ?? [];
            var allow = rules.Values<string>()
                .Concat(
                    [
                        ".minecraft.net",
                        ".mojang.com"
                    ]
                )
                .Any(
                    rule => rule is not null && (rule.StartsWith('.')
                        ? uri.Host.EndsWith(rule, StringComparison.OrdinalIgnoreCase)
                        : Regex.IsMatch(
                            uri.Host,
                            "^" + Regex.Escape(rule)
                                .Replace("\\*", ".*") + "$",
                            RegexOptions.IgnoreCase,
                            TimeSpan.FromSeconds(1)
                        ))
                );
            if (!allow)
                throw new InvalidDataException("皮肤网址不在认证服务器声明的域名范围内。");
            return uri.AbsoluteUri;
        }

        return new(
            Url("SKIN"),
            Url("CAPE"),
            (string? )data["textures"]?["SKIN"]?["metadata"]?["model"] ?? "default",
            uploadable
        );
    }

    // [实现] ChangeTextureAsync 的实现：按 kind is not ("skin" or "cape") 分支处理；准备 var textures = await TexturesAsync(login.Server, login.Credential.Session.Uuid, ct)；按 !textures.Uploadable.Contains(kind) 分支处理。
    public async Task ChangeTextureAsync(
        ExternalLogin login,
        string kind,
        byte[]? png,
        bool slim = false,
        CancellationToken ct = default
    )
    {
        if (kind is not ("skin" or "cape"))
            throw new ArgumentException("未知材质类型。");
        var textures = await TexturesAsync(login.Server, login.Credential.Session.Uuid, ct);
        if (!textures.Uploadable.Contains(kind))
            throw new InvalidOperationException(
                "此认证服务器没有授权该角色修改" + (kind == "skin"
                    ? "皮肤"
                    : "披风") + "。"
            );
        using var request = Request(
            png is null
                ? HttpMethod.Delete
                : HttpMethod.Put,
            login.Server,
            "api/user/profile/" + login.Credential.Session.Uuid + "/" + kind
        );
        request.Headers.Authorization = new("Bearer", login.Credential.Session.AccessToken);
        if (png is not null)
        {
            // [实现] 条件成立（png is not null）时：执行 ValidatePng(png, kind)；准备 var content = new MultipartFormDataContent()；按 kind == "skin" 分支处理。
            ValidatePng(png, kind);
            var content = new MultipartFormDataContent();
            if (kind == "skin")
                content.Add(
                    new StringContent(
                        slim
                            ? "slim"
                            : ""
                    ),
                    "model"
                );
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new("image/png");
            content.Add(file, "file", kind + ".png");
            request.Content = content;
        }

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NoContent)
            throw ResponseError(response.StatusCode);
    }

    // [实现] DownloadTextureAsync 的实现：准备 var response = await http.GetAsync( Https(address), HttpCompletionOption.ResponseHeadersRead, c…；执行 response.EnsureSuccessStatusCode()；按 response.Content.Headers.ContentType?.MediaType != "image/png" || response.Content.Headers.Cont… 分支处理。
    public async Task<byte[]> DownloadTextureAsync(string address, string kind, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(
            Https(address),
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "image/png" || response.Content.Headers.ContentLength > 4 * 1024 * 1024)
            throw new InvalidDataException("皮肤服务没有返回有效 PNG。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            // [实现] 循环满足 (count = await input.ReadAsync(buffer, ct)) > 0 时：按 output.Length + count > 4 * 1024 * 1024 分支处理；执行 output.Write(buffer, 0, count)。
            if (output.Length + count > 4 * 1024 * 1024)
                throw new InvalidDataException("皮肤图片过大。");
            output.Write(buffer, 0, count);
        }

        var bytes = output.ToArray();
        ValidatePng(bytes, kind);
        return bytes;
    }

    // [实现] ValidatePng 的实现：按 bytes.Length is < 33 or > 4 * 1024 * 1024 || !bytes.AsSpan(0, 8) .SequenceEqual( new byte[] { 1… 分支处理；准备 var width = BinaryPrimitives.ReadUInt32BigEndian( bytes.AsSpan(16, 4) )；准备 var height = BinaryPrimitives.ReadUInt32BigEndian( bytes.AsSpan(20, 4) )。
    public static void ValidatePng(byte[] bytes, string kind)
    {
        if (bytes.Length is < 33 or > 4 * 1024 * 1024 || !bytes.AsSpan(0, 8)
            .SequenceEqual(
                new byte[]
                {
                    137,
                    80,
                    78,
                    71,
                    13,
                    10,
                    26,
                    10
                }
            ) || Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR")
            throw new InvalidDataException("请选择有效的 PNG 文件（不超过 4 MiB）。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(16, 4)
        );
        var height = BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(20, 4)
        );
        var valid = width is> 0 and <= 4096 && height is> 0 and <= 4096 && (kind == "skin"
            ? width % 64 == 0 && (height == width || height * 2 == width)
            : width % 64 == 0 && height * 2 == width);
        if (!valid)
            throw new InvalidDataException("皮肤需为 64×64 或 64×32 的整数倍；披风需为 64×32 的整数倍。");
    }

    // [实现] Login 的实现：计算并返回 new( server, identifier, new( new(profile.Name, profile.Id, access, "mojang", false), client ),…。
    private static ExternalLogin Login(
        ExternalServer server,
        string identifier,
        string access,
        string client,
        ExternalProfile profile,
        IReadOnlyList<ExternalProfile> profiles
    ) => new(
        server,
        identifier,
        new(
            new(profile.Name, profile.Id, access, "mojang", false),
            client
        ),
        profiles
    );
    // [实现] ReadProfile 的实现：准备 var result = new ExternalProfile( ((string? )value["id"] ?? "").ToLowerInvariant(), (string? )v…；执行 ValidateProfile(result)；返回 result。
    private static ExternalProfile ReadProfile(JToken value)
    {
        var result = new ExternalProfile(
            ((string? )value["id"] ?? "").ToLowerInvariant(),
            (string? )value["name"] ?? ""
        );
        ValidateProfile(result);
        return result;
    }

    // [实现] ValidateProfile 的实现：按 !Regex.IsMatch(profile.Id, "^[a-fA-F0-9]{32}$") || profile.Name.Length is < 1 or > 64 || profil… 分支处理。
    private static void ValidateProfile(ExternalProfile profile)
    {
        if (!Regex.IsMatch(profile.Id, "^[a-fA-F0-9]{32}$") || profile.Name.Length is < 1 or > 64 || profile.Name.Any(char.IsControl))
            throw new InvalidDataException("外置角色信息无效。");
    }

    // [实现] Token 的实现：准备 var value = (string? )json[name]；返回 value is { Length: > 0 and < 16384 } && !value.Any(char.IsControl) ? value : throw new InvalidD…。
    private static string Token(JObject json, string name)
    {
        var value = (string? )json[name];
        return value is { Length: > 0 and < 16384 } && !value.Any(char.IsControl)
            ? value
            : throw new InvalidDataException("认证令牌无效。");
    }

    // [实现] Request 的实现：准备 var root = Https(server.ApiRoot)；准备 var request = new HttpRequestMessage( method, new Uri( root.AbsoluteUri.TrimEnd('/') + "/" + pa…；按 body is not null 分支处理。
    private static HttpRequestMessage Request(HttpMethod method, ExternalServer server, string path, object? body = null)
    {
        var root = Https(server.ApiRoot);
        var request = new HttpRequestMessage(
            method,
            new Uri(
                root.AbsoluteUri.TrimEnd('/') + "/" + path
            )
        );
        if (body is not null)
            request.Content = new StringContent(
                JsonConvert.SerializeObject(body),
                Encoding.UTF8,
                "application/json"
            );
        return request;
    }

    // [实现] PostAsync 的实现：准备 var request = Request(HttpMethod.Post, server, path, body)；准备 var response = await http.SendAsync(request, ct)；按 !response.IsSuccessStatusCode 分支处理。
    private async Task<JObject> PostAsync(ExternalServer server, string path, object body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, server, path, body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw ResponseError(response.StatusCode);
        return JObject.Parse(
            await ReadTextAsync(response, ct)
        );
    }

    // [实现] ResponseError 的实现：计算并返回 code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? new LoginRequiredException("外…。
    private static Exception ResponseError(HttpStatusCode code) => code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? new LoginRequiredException("外置认证已失效或账号密码不正确，请重新登录。")
        : new HttpRequestException($"外置认证请求失败（{(int)code}），请检查服务器。", null, code);
    // [实现] ReadTextAsync 的实现：按 response.Content.Headers.ContentLength > 1024 * 1024 分支处理；准备 var stream = await response.Content.ReadAsStreamAsync(ct)；准备 var data = new MemoryStream()。
    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 1024 * 1024)
            throw new InvalidDataException("认证响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            // [实现] 循环满足 (count = await stream.ReadAsync(buffer, ct)) > 0 时：按 data.Length + count > 1024 * 1024 分支处理；执行 data.Write(buffer, 0, count)。
            if (data.Length + count > 1024 * 1024)
                throw new InvalidDataException("认证响应过大。");
            data.Write(buffer, 0, count);
        }

        return Encoding.UTF8.GetString(
            data.ToArray()
        );
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
