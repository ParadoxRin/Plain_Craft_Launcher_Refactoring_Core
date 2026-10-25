using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class AccountAppearanceTests
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

        const string uuid = "11111111111111111111111111111111", skinId = "22222222-2222-2222-2222-222222222222", capeId = "33333333-3333-3333-3333-333333333333";
        var session = new GameSession("OwnedPlayer", uuid, "fixture-game-token-never-log", "msa", false);
        var received = new List<(string Method, string Path, string Body)>();
        var profile = new JObject
        {
            ["id"] = uuid,
            ["name"] = session.Name,
            ["skins"] = new JArray(
                new JObject
                {
                    ["id"] = skinId,
                    ["state"] = "ACTIVE",
                    ["url"] = "http://textures.minecraft.net/texture/fixture",
                    ["variant"] = "SLIM"
                }
            ),
            ["capes"] = new JArray(
                new JObject
                {
                    ["id"] = capeId,
                    ["state"] = "INACTIVE",
                    ["url"] = "https://textures.minecraft.net/texture/cape",
                    ["alias"] = "Owned Cape"
                }
            )
        };
        using var skins = new MicrosoftSkinService(
            new Handler(
                async request =>
                {
                    Check(
                        request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "api.minecraftservices.com",
                        "official API only"
                    );
                    Check(
                        request.Headers.Authorization?.ToString() == "Bearer " + session.AccessToken && !request.RequestUri.Query.Contains(session.AccessToken),
                        "bearer header only"
                    );
                    received.Add(
                        (request.Method.Method, request.RequestUri.AbsolutePath, request.Content is null
                            ? ""
                            : await request.Content.ReadAsStringAsync())
                    );
                    return request.Method == HttpMethod.Get
                        ? new(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                profile.ToString(),
                                Encoding.UTF8,
                                "application/json"
                            )
                        }

                        : new(HttpStatusCode.NoContent);
                }
            )
        );
        await test(
            "microsoft_skin_profile_upload_selection_reset_and_owned_cape",
            async () =>
            {
                var appearance = await skins.GetAsync(session);
                Check(
                    appearance.Skins.Single()
                        .Url.StartsWith("https://") && appearance.Capes.Single()
                        .Name == "Owned Cape",
                    "profile parsed and texture HTTPS upgraded"
                );
                await skins.UploadSkinAsync(
                    session,
                    Png(64, 64),
                    true
                );
                var upload = received.Last();
                Check(
                    upload.Method == "POST" && upload.Path == "/minecraft/profile/skins" && upload.Body.Contains("slim") && upload.Body.Contains("image/png") && upload.Body.Contains("skin.png"),
                    "upload multipart shape"
                );
                await skins.UseSkinAsync(session, skinId);
                Check(
                    (string? )JObject.Parse(
                        received.Last()
                            .Body
                    )["variant"] == "slim",
                    "existing skin selected through URL API"
                );
                await skins.UseCapeAsync(session, capeId);
                Check(
                    received.Last()
                        .Method == "PUT" && (string? )JObject.Parse(
                        received.Last()
                            .Body
                    )["capeId"] == capeId,
                    "owned cape selected"
                );
                var writes = received.Count(
                    x => x.Method != "GET"
                );
                await Throws<InvalidOperationException>(
                    () => skins.UseCapeAsync(session, "not-owned")
                );
                Check(
                    received.Count(
                        x => x.Method != "GET"
                    ) == writes,
                    "unowned cape never posted"
                );
                await skins.ResetSkinAsync(session);
                Check(
                    received.Last()
                        .Method == "DELETE" && received.Last()
                        .Path == "/minecraft/profile/skins/active",
                    "reset skin endpoint"
                );
                await skins.HideCapeAsync(session);
                Check(
                    received.Last()
                        .Method == "DELETE" && received.Last()
                        .Path == "/minecraft/profile/capes/active",
                    "hide cape endpoint"
                );
            }
        );
        await test(
            "microsoft_skin_rejects_offline_wrong_identity_and_expired_token",
            async () =>
            {
                var before = received.Count;
                await Throws<LoginRequiredException>(
                    () => skins.GetAsync(
                        AccountManager.OfflineSession("Player")
                    )
                );
                Check(received.Count == before, "offline never calls API");
                profile["id"] = "44444444444444444444444444444444";
                await Throws<LoginRequiredException>(
                    () => skins.GetAsync(session)
                );
                profile["id"] = uuid;
                await Throws<InvalidDataException>(
                    () => skins.UploadSkinAsync(
                        session,
                        Png(128, 128),
                        false
                    )
                );
                using var denied = new MicrosoftSkinService(
                    new Handler(
                        _ => Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.Unauthorized)
                            {
                                Content = new StringContent(session.AccessToken)
                            }
                        )
                    )
                );
                try
                {
                    await denied.GetAsync(session);
                    throw new Exception("expected token rejection");
                }
                catch (LoginRequiredException ex)
                {
                    Check(
                        !ex.Message.Contains(session.AccessToken),
                        "token never echoed"
                    );
                }
            }
        );
        var paths = new AppPaths(
            Path.Combine(
                root,
                "appearance-" + Guid.NewGuid()
                    .ToString("N")
            )
        );
        var accounts = new AccountManager(
            paths,
            new MemorySecrets()
        );
        var account = accounts.AddOffline("LocalSkin");
        var offline = new OfflineSkinService(paths);
        var game = Path.Combine(paths.Root, "instance");
        Directory.CreateDirectory(game);
        var jar = Path.Combine(paths.Root, "client.jar");
        void Client(JToken? format, params string[] textures)
        {
            if (File.Exists(jar))
                File.Delete(jar);
            using var archive = ZipFile.Open(jar, ZipArchiveMode.Create);
            if (format is not null)
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("version.json")
                        .Open()
                );
                writer.Write(
                    new JObject
                    {
                        ["pack_version"] = format
                    }
                );
            }

            foreach (var texture in textures)
            {
                using var output = archive.CreateEntry(texture)
                    .Open();
                output.Write(
                    Png(64, 64)
                );
            }
        }

        var version = new InstalledVersion(
            "copy-fixture",
            "unused.json",
            jar,
            "unused",
            [],
            21
        );
        await test(
            "offline_skin_pack_uses_client_metadata_preserves_uuid_and_options",
            () =>
            {
                var originalUuid = account.Uuid;
                offline.Save(
                    account,
                    Png(64, 64)
                );
                Client(
                    new JObject
                    {
                        ["resource"] = 34
                    },
                    "assets/minecraft/textures/entity/player/wide/steve.png",
                    "assets/minecraft/textures/entity/player/slim/alex.png"
                );
                File.WriteAllText(
                    Path.Combine(game, "options.txt"),
                    "music:0.3\nresourcePacks:[\"vanilla\",\"file/Existing.zip\"]\n"
                );
                offline.Prepare(account, version, game, "1.21.1");
                using (var pack = ZipFile.OpenRead(
                    Path.Combine(game, "resourcepacks", OfflineSkinService.PackName)
                ))
                {
                    using var reader = new StreamReader(
                        pack.GetEntry("pack.mcmeta")!.Open()
                    );
                    Check(
                        (int? )JObject.Parse(
                            reader.ReadToEnd()
                        )["pack"]?["pack_format"] == 34,
                        "format from installed client"
                    );
                    Check(
                        pack.Entries.Count == 3 && pack.GetEntry("assets/minecraft/textures/entity/player/slim/alex.png")is not null,
                        "modern default variants included"
                    );
                }

                var options = File.ReadAllText(
                    Path.Combine(game, "options.txt")
                );
                Check(
                    options.Contains("music:0.3") && options.Contains("file/Existing.zip") && options.Contains("file/" + OfflineSkinService.PackName),
                    "other settings and pack order preserved"
                );
                Check(
                    account.Uuid == originalUuid && accounts.Accounts.Single()
                        .Uuid == originalUuid,
                    "world identity unchanged"
                );
                offline.Prepare(null, version, game, "1.21.1");
                Check(
                    !File.Exists(
                        Path.Combine(game, "resourcepacks", OfflineSkinService.PackName)
                    ) && File.ReadAllText(
                        Path.Combine(game, "options.txt")
                    )
                        .Contains("file/Existing.zip"),
                    "official/demo account removes only launcher skin pack"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "offline_skin_legacy_and_new_major_minor_pack_formats",
            () =>
            {
                Client(
                    null,
                    "assets/minecraft/textures/entity/steve.png",
                    "assets/minecraft/textures/entity/alex.png"
                );
                offline.Prepare(account, version, game, "1.12.2");
                using (var pack = ZipFile.OpenRead(
                    Path.Combine(game, "resourcepacks", OfflineSkinService.PackName)
                ))
                {
                    using var reader = new StreamReader(
                        pack.GetEntry("pack.mcmeta")!.Open()
                    );
                    Check(
                        (int? )JObject.Parse(
                            reader.ReadToEnd()
                        )["pack"]?["pack_format"] == 3,
                        "legacy format fallback"
                    );
                }

                Check(
                    !File.ReadAllText(
                        Path.Combine(game, "options.txt")
                    )
                        .Contains("file/" + OfflineSkinService.PackName),
                    "old resource pack identifier"
                );
                Client(
                    new JObject
                    {
                        ["resource_major"] = 97,
                        ["resource_minor"] = 1
                    },
                    "assets/minecraft/textures/entity/player/wide/steve.png"
                );
                offline.Prepare(account, version, game, "26.3");
                using (var pack = ZipFile.OpenRead(
                    Path.Combine(game, "resourcepacks", OfflineSkinService.PackName)
                ))
                {
                    using var reader = new StreamReader(
                        pack.GetEntry("pack.mcmeta")!.Open()
                    );
                    var metadata = JObject.Parse(
                        reader.ReadToEnd()
                    )["pack"]!;
                    Check(
                        metadata["min_format"]!.Values<int>()
                            .SequenceEqual(
                                new[]
                                {
                                    97,
                                    1
                                }
                            ) && JToken.DeepEquals(metadata["min_format"], metadata["max_format"]),
                        "new major minor bounds"
                    );
                }

                return Task.CompletedTask;
            }
        );
        await test(
            "offline_skin_corruption_and_filename_collision_preserve_user_files",
            async () =>
            {
                var skinFile = offline.ImagePath(account)!;
                File.AppendAllText(skinFile, "corrupt");
                await Throws<InvalidDataException>(
                    () => Task.Run(
                        () => offline.Prepare(account, version, game, "26.3")
                    )
                );
                offline.Save(
                    account,
                    Png(64, 64)
                );
                var target = Path.Combine(game, "resourcepacks", OfflineSkinService.PackName);
                File.Delete(
                    Path.Combine(game, ".pcl-offline-skin.json")
                );
                File.WriteAllText(target, "user-content");
                await Throws<IOException>(
                    () => Task.Run(
                        () => offline.Prepare(account, version, game, "26.3")
                    )
                );
                Check(
                    File.ReadAllText(target) == "user-content",
                    "unowned file unchanged"
                );
                var malicious = new LauncherAccount("microsoft-" + uuid, AccountKind.Microsoft, "Microsoft", uuid);
                await Throws<InvalidOperationException>(
                    () => Task.Run(
                        () => offline.Save(
                            malicious,
                            Png(64, 64)
                        )
                    )
                );
            }
        );
        await test(
            "java_discovery_rejects_escaping_receipts_and_macos_stub",
            () =>
            {
                var runtime = Path.Combine(paths.Runtime, "java-fixture");
                Directory.CreateDirectory(runtime);
                var exe = Path.Combine(runtime, "java");
                File.WriteAllText(exe, "fixture");
                JsonFiles.Write(
                    Path.Combine(runtime, "pcl-java.json"),
                    new
                    {
                        java = "java"
                    }
                );
                Check(
                    JavaDiscovery.Candidates(
                        paths,
                        [
                            exe,
                            "/usr/bin/java"
                        ]
                    )
                        .Count(
                            x => x == exe
                        ) == 1,
                    "known runtime deduplicated"
                );
                Check(
                    !JavaDiscovery.Candidates(
                        paths,
                        [
                            "/usr/bin/java"
                        ]
                    )
                        .Contains("/usr/bin/java"),
                    "mac install stub excluded"
                );
                JsonFiles.Write(
                    Path.Combine(runtime, "pcl-java.json"),
                    new
                    {
                        java = "../../../outside/java"
                    }
                );
                Check(
                    !JavaDiscovery.Candidates(paths)
                        .Contains(exe),
                    "escaping runtime receipt ignored"
                );
                return Task.CompletedTask;
            }
        );
    }

    // Real, decodable PNG fixture, including zlib data and CRCs; no graphics dependency in Core tests.
    public static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(
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
        );
        void Chunk(string name, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var kind = Encoding.ASCII.GetBytes(name);
            output.Write(kind);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (var value in kind.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 1
                        ? 0xedb88320U
                        : 0);
            }

            BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            output.Write(length);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(
            header.AsSpan(4),
            height
        );
        header[8] = 8;
        header[9] = 6;
        Chunk("IHDR", header);
        using var encoded = new MemoryStream();
        using (var zip = new ZLibStream(encoded, CompressionLevel.Fastest, true))
            for (var y = 0; y < height; y++)
            {
                zip.WriteByte(0);
                for (var x = 0; x < width; x++)
                    zip.Write(
                        new byte[]
                        {
                            (byte)(x * 3),
                            (byte)(y * 3),
                            180,
                            255
                        }
                    );
            }

        Chunk(
            "IDAT",
            encoded.ToArray()
        );
        Chunk(
            "IEND",
            []
        );
        return output.ToArray();
    }

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
        public string? Get(string id) => null;
        public void Set(string id, string secret)
        {
        }

        public void Delete(string id)
        {
        }
    }
}
