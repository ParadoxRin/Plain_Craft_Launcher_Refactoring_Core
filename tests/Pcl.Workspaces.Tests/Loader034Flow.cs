using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Workspaces;

/// <summary>加载器实际启动使用隔离实例副本；NeoForge额外从公开源安装真实Sodium，覆盖嵌套JarJar生产路径。</summary>
internal static class Loader034Flow
{
    public static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        var evidence = new List<object>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")!;
        await using var session = new LauncherSession(
            new HostClient(native, "", root, reconnectable: true)
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
                    name = "PCL034_Loaders"
                }
            );
            foreach (var instance in new[]
            {
                "forge-1.21.1-52.1.20",
                "neoforge-1.21.1-21.1.264"
            }

            )
            {
                var loader = instance.StartsWith("neoforge")
                    ? "neoforge"
                    : "forge";
                if (loader == "neoforge")
                {
                    using var catalog = new Pcl.Core.ResourceCatalog(
                        paths: new Pcl.Core.AppPaths(root)
                    );
                    var release = (await catalog.VersionsAsync("sodium", Pcl.Core.ResourceKind.Mod, "1.21.1", "neoforge")).First(
                        x => x.Channel == "release"
                    );
                    await api.CallAsync<bool>(
                        "workspace.browser.install",
                        instance,
                        new
                        {
                            releaseId = release.Id,
                            kind = (int)ResourceKind.Mod
                        }
                    );
                    Check(true, "neoforge-real-sodium-downloaded-and-installed");
                    var selection = Pcl.Core.JarJarResolver.Resolve(
                        Directory.GetFiles(
                            Path.Combine(root, "instances", instance, "mods"),
                            "*.jar"
                        ),
                        loader,
                        "1.21.1",
                        21
                    );
                    Check(selection.Selected.Count > 0, "neoforge-real-sodium-nested-jarjar-selected");
                    evidence.Add(
                        new
                        {
                            release.Id,
                            release.Number,
                            release.File.Sha1,
                            selectedNested = selection.Selected.Select(
                                x => new
                                {
                                    x.Coordinate,
                                    x.Version
                                }
                            )
                                .ToArray()
                        }
                    );
                }

                await api.CallAsync<string[]>("workspace.resources.validate", instance);
                Check(true, loader + "-host-dependency-validation");
                var accepted = await session.Launches.AcceptAsync(
                    new GameLaunchCommand(
                        Guid.NewGuid()
                            .ToString("N"),
                        account.Id,
                        instance
                    )
                );
                var result = await session.Launches.WaitAsync(accepted.OperationId)
                    .WaitAsync(
                        TimeSpan.FromMinutes(3)
                    );
                Check(result.State == "succeeded", loader + "-launch-" + result.ErrorCode);
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
                    while (DateTime.UtcNow < until)
                    {
                        var log = File.Exists(details.LogFile)
                            ? File.ReadAllText(details.LogFile)
                            : "";
                        rendered = log.Contains("Created:") && log.Contains("atlas");
                        if (rendered || (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                        {
                            break;
                        }

                        await Task.Delay(300);
                    }

                    Check(rendered, loader + "-minecraft-renderer-initialized");
                    await Task.Delay(5000);
                    Check(
                        (await session.Launches.GetGameAsync(game.GameProcessId)).State != "exited",
                        loader + "-alive-after-five-seconds"
                    );
                    evidence.Add(
                        new
                        {
                            loader,
                            instance,
                            game.Pid,
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
                        loader + "-owned-game-stopped"
                    );
                }
            }

            await session.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        evidence
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
                        evidence,
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
