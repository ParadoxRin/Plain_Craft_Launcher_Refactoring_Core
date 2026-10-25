using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class ExternalAuthenticationTests
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value, string message)
        {
            if (!value)
                throw new Exception(message);
        }

        static async Task Throws<T>(Func<Task> work)
        where T : Exception
        {
            try
            {
                await work();
            }
            catch (T)
            {
                return;
            }

            throw new Exception("Expected " + typeof(T).Name);
        }

        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var serverMetadata = new JObject
        {
            ["meta"] = new JObject
            {
                ["serverName"] = "测试认证"
            },
            ["skinDomains"] = new JArray("textures.example.test"),
            ["signaturePublickey"] = publicKey
        };
        const string uuid = "11111111111111111111111111111111";
        const string other = "22222222222222222222222222222222";
        string client = "";
        bool valid = false;
        int refreshes = 0;
        string? authorization = null;
        var handler = new Handler(
            async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == "/")
                    return Json(serverMetadata);
                if (request.Method == HttpMethod.Get && path.EndsWith("/profile/" + uuid))
                    return Json(
                        new JObject
                        {
                            ["id"] = uuid,
                            ["name"] = "Role",
                            ["properties"] = new JArray(
                                new JObject
                                {
                                    ["name"] = "uploadableTextures",
                                    ["value"] = "skin,cape"
                                },
                                new JObject
                                {
                                    ["name"] = "textures",
                                    ["value"] = Convert.ToBase64String(
                                        Encoding.UTF8.GetBytes(
                                            new JObject
                                            {
                                                ["profileId"] = uuid,
                                                ["textures"] = new JObject
                                                {
                                                    ["SKIN"] = new JObject
                                                    {
                                                        ["url"] = "https://textures.example.test/skin.png",
                                                        ["metadata"] = new JObject
                                                        {
                                                            ["model"] = "slim"
                                                        }
                                                    }
                                                }
                                            }.ToString()
                                        )
                                    )
                                }
                            )
                        }
                    );
                var body = request.Content is not null && request.Content.Headers.ContentType?.MediaType == "application/json"
                    ? JObject.Parse(
                        await request.Content.ReadAsStringAsync()
                    )
                    : new JObject();
                if (path == "/authserver/authenticate")
                {
                    Check(
                        (bool? )body["requestUser"] == true && (string? )body["agent"]?["name"] == "Minecraft",
                        "authenticate protocol"
                    );
                    client = (string)body["clientToken"]!;
                    return Json(
                        new JObject
                        {
                            ["accessToken"] = "initial-secret-token",
                            ["clientToken"] = client,
                            ["availableProfiles"] = new JArray(
                                new JObject
                                {
                                    ["id"] = uuid,
                                    ["name"] = "Role"
                                },
                                new JObject
                                {
                                    ["id"] = other,
                                    ["name"] = "Other"
                                }
                            )
                        }
                    );
                }

                if (path == "/authserver/validate")
                    return new(
                        valid
                            ? HttpStatusCode.NoContent
                            : HttpStatusCode.Forbidden
                    );
                if (path == "/authserver/refresh")
                {
                    refreshes++;
                    Check(
                        (bool? )body["requestUser"] == true && (string? )body["clientToken"] == client,
                        "refresh protocol"
                    );
                    if (body["selectedProfile"] is not null)
                        Check((string? )body["selectedProfile"]?["id"] == uuid, "selected role");
                    valid = true;
                    return Json(
                        new JObject
                        {
                            ["accessToken"] = "rotated-secret-token-" + refreshes,
                            ["clientToken"] = client,
                            ["selectedProfile"] = new JObject
                            {
                                ["id"] = uuid,
                                ["name"] = "Role"
                            }
                        }
                    );
                }

                if (path == "/authserver/invalidate")
                {
                    valid = false;
                    return new(HttpStatusCode.NoContent);
                }

                if (path.StartsWith("/api/user/profile/"))
                {
                    authorization = request.Headers.Authorization?.ToString();
                    return new(HttpStatusCode.NoContent);
                }

                throw new Exception("Unexpected mock endpoint " + path);
            }
        );
        using var auth = new ExternalAuthentication(handler);
        var paths = new AppPaths(
            Path.Combine(
                root,
                "external-auth-" + Guid.NewGuid()
                    .ToString("N")
            )
        );
        var secrets = new MemorySecrets();
        var accounts = new AccountManager(paths, secrets);
        ExternalLogin? login = null;
        LauncherAccount? account = null;
        await test(
            "external_https_discovery_and_no_password_redirect",
            async () =>
            {
                Check(
                    (await auth.DiscoverAsync("auth.example.test")).ApiRoot == "https://auth.example.test/",
                    "https autocompletion"
                );
                await Throws<InvalidDataException>(
                    () => auth.DiscoverAsync("http://auth.example.test")
                );
                int calls = 0;
                using var redirected = new ExternalAuthentication(
                    new Handler(
                        request =>
                        {
                            calls++;
                            var response = new HttpResponseMessage(HttpStatusCode.Found);
                            response.Headers.Location = new("http://insecure.example.test/");
                            return Task.FromResult(response);
                        }
                    )
                );
                await Throws<InvalidDataException>(
                    () => redirected.DiscoverAsync("https://alias.example.test")
                );
                Check(calls == 1, "no insecure follow");
                calls = 0;
                await Throws<HttpRequestException>(
                    () => redirected.AuthenticateAsync(
                        new("https://auth.example.test/", "Test", "{}"),
                        "account",
                        "mock-password"
                    )
                );
                Check(calls == 1, "credential POST redirect rejected");
            }
        );
        await test(
            "external_multirole_binding_keychain_identity_and_refresh",
            async () =>
            {
                var server = await auth.DiscoverAsync("https://auth.example.test/");
                var challenge = await auth.AuthenticateAsync(server, "account", "mock-password");
                Check(challenge.Profiles.Count == 2 && challenge.Selected is null, "role chooser needed");
                login = await auth.SelectProfileAsync(challenge, uuid);
                account = accounts.SaveExternal(login, true);
                Check(
                    account.Id != ExternalAuthentication.Identity("https://other.example.test", "account", uuid) && account.Id != ExternalAuthentication.Identity(server.ApiRoot, "other-account", uuid),
                    "three part identity"
                );
                var onDisk = File.ReadAllText(
                    Path.Combine(paths.Root, "accounts.json")
                );
                Check(
                    !onDisk.Contains("secret-token") && !onDisk.Contains("mock-password"),
                    "tokens absent index"
                );
                Check(
                    secrets.Values.Values.Single()
                        .Contains("rotated-secret-token"),
                    "token saved in secrets provider"
                );
                valid = false;
                var restarted = new AccountManager(paths, secrets);
                var resumed = await restarted.ResolveExternalAsync(account.Id, auth);
                Check(
                    refreshes == 2 && resumed.Credential.Session.Uuid == uuid && resumed.Credential.Session.UserType == "mojang",
                    "validate refresh and identity preserved"
                );
                Check(
                    !secrets.Values.Values.Single()
                        .Contains("mock-password"),
                    "password not persisted"
                );
            }
        );
        await test(
            "external_skin_capability_bearer_and_png_bounds",
            async () =>
            {
                var textures = await auth.TexturesAsync(login!.Server, uuid);
                Check(
                    textures.Uploadable.Count == 2 && textures.SkinUrl == "https://textures.example.test/skin.png" && textures.Model == "slim",
                    "texture metadata and capabilities"
                );
                var png = new byte[33];
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
                }.CopyTo(png, 0);
                Encoding.ASCII.GetBytes("IHDR")
                    .CopyTo(png, 12);
                BinaryPrimitives.WriteUInt32BigEndian(
                    png.AsSpan(16),
                    64
                );
                BinaryPrimitives.WriteUInt32BigEndian(
                    png.AsSpan(20),
                    64
                );
                await auth.ChangeTextureAsync(login, "skin", png, true);
                Check(
                    authorization == "Bearer " + login.Credential.Session.AccessToken,
                    "token only in authorization header"
                );
                BinaryPrimitives.WriteUInt32BigEndian(
                    png.AsSpan(16),
                    100000
                );
                await Throws<InvalidDataException>(
                    () => auth.ChangeTextureAsync(login, "skin", png)
                );
                var plan = ExternalAuthentication.AddJvmArguments(
                    new(
                        "java",
                        [
                            "-Xmx2G",
                            "Main"
                        ],
                        "/tmp"
                    ),
                    [
                        "-javaagent:/agent.jar=https://auth.example.test/",
                        "-Dauthlibinjector.yggdrasil.prefetched=e30="
                    ]
                );
                Check(
                    plan.Arguments[0].StartsWith("-javaagent:") && plan.Arguments.Last() == "Main",
                    "agent precedes main class"
                );
            }
        );
        await test(
            "external_logout_revokes_and_removes_saved_credential",
            async () =>
            {
                await accounts.LogoutExternalAsync(account!.Id, auth);
                Check(
                    !accounts.Accounts.Single()
                        .Remembered && secrets.Values.Count == 0 && !valid,
                    "local and remote logout"
                );
                await Throws<LoginRequiredException>(
                    () => accounts.ResolveExternalAsync(account.Id, auth)
                );
            }
        );
    }

    private static HttpResponseMessage Json(JObject json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            json.ToString(),
            Encoding.UTF8,
            "application/json"
        )
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action(request);
        }
    }

    private sealed class MemorySecrets : IAccountSecrets
    {
        public Dictionary<string, string> Values { get; } = new();

        public string? Get(string id) => Values.GetValueOrDefault(id);
        public void Set(string id, string secret) => Values[id] = secret;
        public void Delete(string id) => Values.Remove(id);
    }
}
