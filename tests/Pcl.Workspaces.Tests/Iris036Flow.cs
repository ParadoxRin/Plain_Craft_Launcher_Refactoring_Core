using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using View = Pcl.Core.Contracts.Workspaces;

/// <summary>独立 Host 从公开源安装有归属的真实 Iris 环境；用户操作隔离世界，按进程/日志/存档哈希验收。</summary>
internal static class Iris036Flow
{
    public static async Task Run(string root, string output, string worldSource)
    {
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        if (!root.Contains("verification-0.36"))
        {
            throw new InvalidOperationException("ISOLATED_ROOT_REQUIRED");
        }

        var checks = new List<string>();
        var sessions = new List<object>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        void Write(string name, object data) => File.WriteAllText(
            Path.Combine(output, name),
            JsonSerializer.Serialize(
                data,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
        Dictionary<string, string> Inventory(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(
                x => Path.GetFileName(x) != "session.lock"
            )
            .Order()
            .ToDictionary(
                x => Path.GetRelativePath(directory, x),
                x =>
                {
                    using var stream = File.OpenRead(x);
                    return Convert.ToHexString(
                        SHA256.HashData(stream)
                    );
                }
            );
        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")!;
        await using var session = new LauncherSession(
            new HostClient(native, "", root, reconnectable: true)
        );
        string? active = null;
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var setup = await api.CallAsync<View.GameProfile>(
                "workspace.desktop.macSetup",
                null,
                new
                {
                    shaders = true
                }
            );
            Check(
                File.Exists(
                    Path.Combine(setup.GameDirectory, ".pcl-iris-environment.json")
                ),
                "real-pinned-environment-installed-with-provenance"
            );
            var plan = await api.CallAsync<View.ShaderEnvironmentPreview>("workspace.browser.irisPreview", setup.Id);
            Check(
                plan.CanApply && plan.Remove.Any(
                    x => x.Contains("iris")
                ),
                "new-iris-environment-has-actionable-uninstall-plan"
            );
            var world = Path.Combine(setup.GameDirectory, "saves/PCL036_World");
            if (Directory.Exists(world))
            {
                throw new IOException("World fixture already exists");
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(world)!
            );
            var copy = new System.Diagnostics.ProcessStartInfo("/bin/cp")
            {
                UseShellExecute = false
            };
            copy.ArgumentList.Add("-cR");
            copy.ArgumentList.Add(
                Path.GetFullPath(worldSource)
            );
            copy.ArgumentList.Add(world);
            using (var process = System.Diagnostics.Process.Start(copy)!)
            {
                await process.WaitForExitAsync();
                Check(process.ExitCode == 0, "original-world-cloned-not-modified");
            }

            await api.CallAsync<bool>(
                "workspace.instances.saveSettings",
                setup.Id,
                new
                {
                    value = new View.LaunchSettings
                    {
                        QuickPlayWorld = "PCL036_World",
                        MaximumMemoryMb = 3072
                    },
                    fields = new[]
                    {
                        "QuickPlayWorld",
                        "MaximumMemoryMb"
                    }
                }
            );
            var account = await api.CallAsync<View.LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL036_World"
                }
            );
            Dictionary<string, string>? prior = null;
            for (var phase = 1; phase <= 2; phase++)
            {
                var before = Inventory(world);
                if (prior is not null)
                {
                    Check(
                        before.SequenceEqual(prior),
                        "second-process-receives-identical-saved-world-files"
                    );
                }

                Write("world-before-" + phase + ".json", before);
                var accepted = await session.Launches.AcceptAsync(
                    new(
                        Guid.NewGuid()
                            .ToString("N"),
                        account.Id,
                        setup.Id
                    )
                );
                var launched = await session.Launches.WaitAsync(accepted.OperationId)
                    .WaitAsync(
                        TimeSpan.FromMinutes(3)
                    );
                Check(launched.State == "succeeded", "world-" + phase + "-launch-" + launched.ErrorCode);
                active = launched.Result!.GameProcessId;
                var details = await session.Client.Call<GameDetails>(
                    "game.details",
                    new
                    {
                        gameProcessId = active
                    }
                );
                var deadline = DateTime.UtcNow.AddMinutes(3);
                string log = "";
                while (DateTime.UtcNow < deadline)
                {
                    log = File.Exists(details.LogFile)
                        ? File.ReadAllText(details.LogFile)
                        : "";
                    if ((log.Contains("logged in with entity id") || log.Contains("joined the game")) || (await session.Launches.GetGameAsync(active)).State == "exited")
                    {
                        break;
                    }

                    await Task.Delay(500);
                }

                Check(
                    log.Contains("logged in with entity id") || log.Contains("joined the game"),
                    "world-" + phase + "-actual-player-entered"
                );
                Check(
                    log.Contains("Using shaderpack: ComplementaryReimagined"),
                    "world-" + phase + "-iris-selected-real-shaderpack"
                );
                Write(
                    "ready.json",
                    new
                    {
                        phase,
                        setup.Id,
                        setup.GameDirectory,
                        world,
                        details.Pid,
                        details.LogFile,
                        state = "world-entered-awaiting-user"
                    }
                );
                Console.WriteLine("WORLD_READY " + phase);
                deadline = DateTime.UtcNow.AddMinutes(20);
                while ((await session.Launches.GetGameAsync(active)).State != "exited" && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                }

                var exited = await session.Launches.GetGameAsync(active);
                Check(exited.State == "exited" && exited.ExitCode == 0, "world-" + phase + "-normal-game-exit");
                active = null;
                log = File.ReadAllText(details.LogFile);
                Check(
                    log.Contains("Stopping server") && log.Contains("Saving"),
                    "world-" + phase + "-integrated-server-save-and-stop"
                );
                var after = Inventory(world);
                Check(
                    after.TryGetValue("level.dat", out var level) && level != before.GetValueOrDefault("level.dat"),
                    "world-" + phase + "-level-data-saved"
                );
                Check(
                    after.Keys.Any(
                        x => x.StartsWith("region/") && x.EndsWith(".mca")
                    ),
                    "world-" + phase + "-overworld-chunks-exist"
                );
                Write("world-after-" + phase + ".json", after);
                prior = after;
                sessions.Add(
                    new
                    {
                        phase,
                        details.Pid,
                        details.LogFile,
                        exited.ExitCode,
                        changedFiles = after.Keys.Count(
                            x => after[x] != before.GetValueOrDefault(x)
                        )
                    }
                );
                Write(
                    "progress.json",
                    new
                    {
                        checks,
                        sessions
                    }
                );
            }

            Write(
                "result.json",
                new
                {
                    status = "automatic_checks_passed",
                    checks,
                    sessions,
                    profileId = setup.Id,
                    gameDirectory = setup.GameDirectory,
                    userVisualAcceptance = "pending"
                }
            );
            await session.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Write(
                "result.json",
                new
                {
                    status = "failed",
                    checks,
                    sessions,
                    error = ex.ToString()
                }
            );
            if (active is not null)
            {
                try
                {
                    await session.Launches.StopGameAsync(active);
                }
                catch
                {
                }
            }

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
