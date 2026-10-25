using System.Text.Json;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;

// 真实包内 Host 关闭并重新创建，恢复两个旧加载器；只操作已隔离的缓存和实例副本。
var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
var executable = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE") ?? Path.GetFullPath("artifacts/PCL Mac 0.24 Host Preview.app/Contents/MacOS/host/Pcl.Host");
var results = new List<object>();
foreach (var family in new[]
{
    "optifine",
    "liteloader"
}

)
{
    try
    {
        string parent, accountId, marker;
        string attempt = Guid.NewGuid()
            .ToString("N");
        int previousPid;
        await using (var client = new HostClient(executable, "", root))
        {
            previousPid = client.HostPid;
            var hello = await client.HelloAsync();
            var grant = await client.Call<JsonElement>(
                "desktop.acquire",
                new { }
            );
            var paths = new AppPaths(root);
            var accounts = new AccountManager(
                paths,
                new KeychainAccountSecrets(root)
            );
            if (accounts.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new Exception("ISOLATED_OFFLINE_ROOT_REQUIRED");
            }

            accountId = accounts.AddOffline("HostLegacyMac")
                .Id;
            using (var source = new MinecraftService(paths))
            {
                var profile = InstanceRegistry.List(source, true)
                    .First(
                        x => x.Loader == family && x.GameVersion == "1.12.2"
                    );
                var installed = InstanceRegistry.Load(source, profile);
                marker = Path.Combine(profile.GameDirectory, "mods", "host-recovery-marker.txt");
                Directory.CreateDirectory(
                    Path.GetDirectoryName(marker)!
                );
                File.WriteAllText(marker, "preserve-user-file");
                if (family == "liteloader")
                {
                    var library = installed.Libraries.First(
                        x => Path.GetFileName(x.Path)
                            .Contains("liteloader", StringComparison.OrdinalIgnoreCase) && x.Url.Scheme == "https"
                    );
                    File.Delete(library.Path);
                }

                DownloadTasks.For(paths)
                    .Configure(
                        new NetworkOptions
                        {
                            ArtificialDelayMs = 1000
                        }
                    );
            }

            await client.Call<JsonElement>(
                "desktop.release",
                new
                {
                    grant = grant.GetProperty("grant")
                        .GetString()
                }
            );
            parent = (await client.Installations.AcceptAsync(
                new(
                    "cancel-" + attempt,
                    new("1.12.2", family, hello.InstallationTarget!)
                )
            )).OperationId;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (true)
            {
                var row = (await client.Installations.GetAsync(parent))!;
                if (row.Stage == (family == "optifine"
                    ? "installer-running"
                    : "loader-planned") && !row.Terminal)
                {
                    break;
                }

                if (row.Terminal || DateTime.UtcNow > deadline)
                {
                    throw new Exception("MISSED_CANCELLATION:" + row.State + ":" + row.ErrorCode);
                }

                await Task.Delay(10);
            }

            await client.Installations.CancelAsync(parent);
            if ((await client.Installations.WaitAsync(parent)).State != "cancelled")
            {
                throw new Exception("NOT_CANCELLED");
            }

            await client.ShutdownAsync();
        }

        await using (var client = new HostClient(executable, "", root))
        {
            var hello = await client.HelloAsync();
            var accepted = await client.Installations.AcceptAsync(
                new(
                    "resume-" + attempt,
                    new("1.12.2", family, hello.InstallationTarget!),
                    parent
                )
            );
            var row = await client.Installations.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(5)
                );
            if (row.State != "succeeded")
            {
                throw new Exception("RECOVERY_FAILED:" + row.ErrorCode);
            }

            if (File.ReadAllText(marker) != "preserve-user-file")
            {
                throw new Exception("USER_FILE_CHANGED");
            }

            var launch = await client.Launches.AcceptAsync(
                new("launch-" + attempt, accountId, row.Result!.Id)
            );
            var started = await client.Launches.WaitAsync(launch.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(3)
                );
            if (started.Result is null)
            {
                throw new Exception("LAUNCH_FAILED:" + started.ErrorCode);
            }

            var game = started.Result;
            var details = await client.Call<GameDetails>(
                "game.details",
                new
                {
                    gameProcessId = game.GameProcessId
                }
            );
            bool rendered = false;
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline)
                {
                    var text = File.Exists(details.LogFile)
                        ? File.ReadAllText(details.LogFile)
                        : "";
                    if (text.Contains("Created:") && (text.Contains("textures") || text.Contains("atlas")))
                    {
                        rendered = true;
                        break;
                    }

                    if ((await client.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                    {
                        break;
                    }

                    await Task.Delay(250);
                }

                if (!rendered)
                {
                    throw new Exception("RENDERER_NOT_INITIALIZED");
                }
            }
            finally
            {
                await client.Launches.StopGameAsync(game.GameProcessId);
            }

            while ((await client.Launches.GetGameAsync(game.GameProcessId)).State != "exited")
            {
                await Task.Delay(100);
            }

            await client.ShutdownAsync();
            results.Add(
                new
                {
                    family,
                    status = "passed",
                    parent,
                    resumed = row.OperationId,
                    previousPid,
                    newPid = client.HostPid,
                    rendererInitialized = rendered,
                    userFilePreserved = true
                }
            );
        }
    }
    catch (Exception ex)
    {
        results.Add(
            new
            {
                family,
                status = "failed",
                error = DiagnosticsService.Redact(ex.Message)
            }
        );
        Environment.ExitCode = 1;
    }

    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            results,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}
