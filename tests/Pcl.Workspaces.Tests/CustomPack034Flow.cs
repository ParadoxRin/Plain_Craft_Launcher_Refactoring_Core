using System.IO.Compression;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Workspaces;

/// <summary>从真实Client调用随包Host导入MultiMC、HMCL及往返mrpack，再以校验过的自定义类启动真实Minecraft。</summary>
internal static class CustomPack034Flow
{
    public static async Task Run(string root, string fixtures, string report)
    {
        root = Path.GetFullPath(root);
        fixtures = Path.GetFullPath(fixtures);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        var games = new List<object>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        void Zip(string path, params (string Name, string Text)[] entries)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry(entry.Name)
                        .Open()
                );
                writer.Write(entry.Text);
            }
        }

        var library = JObject.Parse(
            File.ReadAllText(
                Path.Combine(fixtures, "library.json")
            )
        );
        var artifact = library["downloads"]!["artifact"]!;
        var cache = Path.Combine(
            root,
            "minecraft/libraries/pcl-custom",
            (string)artifact["sha1"]!,
            (string)artifact["path"]!
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(cache)!
        );
        File.Copy(
            Path.Combine(fixtures, "delegate-1.0.jar"),
            cache,
            true
        );
        // 本地测试组件预置到内容哈希缓存；官方游戏和元数据仍由真实Host正常下载/校验。
        var components = new JArray(
            new JObject
            {
                ["uid"] = "net.minecraft",
                ["version"] = "1.21.1"
            },
            new JObject
            {
                ["uid"] = "pcl.compat",
                ["version"] = "1.0"
            }
        );
        var patch = new JObject
        {
            ["formatVersion"] = 1,
            ["uid"] = "pcl.compat",
            ["version"] = "1.0",
            ["order"] = 100,
            ["mainClass"] = "pcl.compat.Delegate",
            ["libraries"] = new JArray(library),
            ["requires"] = new JArray(
                new JObject
                {
                    ["uid"] = "net.minecraft",
                    ["equals"] = "1.21.1"
                }
            )
        };
        var mmc = Path.Combine(root, "compat034-multimc.zip");
        Zip(
            mmc,
            ("mmc-pack.json", new JObject
            {
                ["formatVersion"] = 1,
                ["components"] = components
            }.ToString()),
            ("patches/pcl.compat.json", patch.ToString()),
            (".minecraft/config/pcl034.txt", "preserve-through-export")
        );
        var official = JObject.Parse(
            File.ReadAllText(
                Path.Combine(root, "minecraft/versions/1.21.1/1.21.1.official.json")
            )
        );
        official["mainClass"] = "pcl.compat.Delegate";
        ((JArray)official["libraries"]!).Add(
            library.DeepClone()
        );
        var hmcl = Path.Combine(root, "compat034-hmcl.zip");
        Zip(
            hmcl,
            ("modpack.json", "{\"name\":\"PCL034-HMCL\",\"gameVersion\":\"1.21.1\"}"),
            ("minecraft/versions/custom/custom.json", official.ToString()),
            ("minecraft/config/pcl034.txt", "preserve-through-export")
        );
        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var session = new LauncherSession(
            new HostClient(
                native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                native is null
                    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                    : "",
                root,
                reconnectable: true
            )
        );
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var account = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL034_Compat"
                }
            );
            async Task<GameProfile> Install(string archive, string name)
            {
                var options = new PackImportOptions("1.21.1");
                var preview = await api.CallAsync<PackInspection>(
                    "workspace.packs.inspect",
                    null,
                    new
                    {
                        path = archive,
                        options
                    }
                );
                Check(preview.Plan.Minecraft == "1.21.1", name + "-host-inspection");
                var profile = await api.CallAsync<GameProfile>(
                    "workspace.packs.install",
                    null,
                    new
                    {
                        handle = preview.Handle,
                        options
                    }
                );
                Check(
                    (string? )JObject.Parse(
                        File.ReadAllText(profile.JsonPath)
                    )["mainClass"] == "pcl.compat.Delegate",
                    name + "-host-installed-entrypoint"
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(profile.GameDirectory, "config/pcl034.txt")
                    ) == "preserve-through-export",
                    name + "-overrides-preserved"
                );
                return profile;
            }

            async Task Launch(GameProfile profile, string name)
            {
                var command = new GameLaunchCommand(
                    Guid.NewGuid()
                        .ToString("N"),
                    account.Id,
                    profile.Id
                );
                var accepted = await session.Launches.AcceptAsync(command);
                var result = await session.Launches.WaitAsync(accepted.OperationId)
                    .WaitAsync(
                        TimeSpan.FromMinutes(3)
                    );
                Check(result.State == "succeeded", name + "-launch-admitted-" + result.ErrorCode);
                var game = result.Result!;
                var details = await session.Client.Call<GameDetails>(
                    "game.details",
                    new
                    {
                        gameProcessId = game.GameProcessId
                    }
                );
                try
                {
                    var until = DateTime.UtcNow.AddMinutes(3);
                    var rendered = false;
                    var delegated = false;
                    while (DateTime.UtcNow < until)
                    {
                        var text = File.Exists(details.LogFile)
                            ? File.ReadAllText(details.LogFile)
                            : "";
                        delegated = text.Contains("PCL034_CUSTOM_ENTRYPOINT");
                        rendered = text.Contains("Created:") && text.Contains("atlas");
                        if (rendered || (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                        {
                            break;
                        }

                        await Task.Delay(300);
                    }

                    Check(delegated, name + "-compiled-custom-main-executed");
                    Check(rendered, name + "-minecraft-renderer-initialized");
                    var replay = await session.Launches.AcceptAsync(command);
                    Check(
                        replay.Replayed && replay.OperationId == accepted.OperationId,
                        name + "-launch-replay-no-second-game"
                    );
                    games.Add(
                        new
                        {
                            name,
                            profile = profile.Id,
                            game.Pid,
                            delegated,
                            rendered,
                            log = Path.GetRelativePath(root, details.LogFile),
                            worldAcceptance = "not-executed"
                        }
                    );
                }
                finally
                {
                    await session.Launches.StopGameAsync(game.GameProcessId);
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while ((await session.Launches.GetGameAsync(game.GameProcessId)).State != "exited" && DateTime.UtcNow < until)
                    {
                        await Task.Delay(100);
                    }

                    Check(
                        (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited",
                        name + "-owned-game-stopped"
                    );
                }
            }

            var first = await Install(mmc, "multimc");
            await Launch(first, "multimc");
            var exported = Path.Combine(root, "compat034-roundtrip.mrpack");
            await api.ExportAsync(
                "workspace.packs.export",
                first.Id,
                new
                {
                    options = new PackExportOptions("PCL034-Roundtrip")
                },
                exported
            );
            using (var zip = ZipFile.OpenRead(exported))
            {
                using var reader = new StreamReader(
                    zip.GetEntry("pcl-custom-components.json")!.Open()
                );
                var extension = JObject.Parse(
                    reader.ReadToEnd()
                );
                Check(
                    (int? )extension["schemaVersion"] == 2 && (string? )extension["mainClass"] == "pcl.compat.Delegate",
                    "mrpack-export-retains-schema2-entrypoint"
                );
            }

            await Launch(
                await Install(exported, "mrpack-roundtrip"),
                "mrpack-roundtrip"
            );
            await Launch(
                await Install(hmcl, "hmcl"),
                "hmcl"
            );
            await session.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        games,
                        customLibrarySource = "locally compiled fixture in SHA1 cache; not a public download"
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        games,
                        error = ex.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
