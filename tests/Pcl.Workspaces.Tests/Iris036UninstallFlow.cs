using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Workspaces;

/// <summary>最终 Host 包卸载独占环境/读取 UI 卸载结果，启动真实 Fabric 游戏验证未产生缺失依赖。</summary>
internal static class Iris036UninstallFlow
{
    internal static async Task Run(string root, string report, bool exclusive)
    {
        root = Path.GetFullPath(root);
        if (!root.Contains("verification-0.36"))
        {
            throw new InvalidOperationException("ISOLATED_ROOT_REQUIRED");
        }

        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        Dictionary<string, string> Protected(string game) => new[]
        {
            "config",
            "shaderpacks",
            "saves"
        }.SelectMany(
            d => Directory.GetFiles(
                Path.Combine(game, d),
                "*",
                SearchOption.AllDirectories
            )
        )
            .Order()
            .ToDictionary(
                x => Path.GetRelativePath(game, x),
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
            var instances = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var profile = instances.Items.Single(
                x => Path.GetFileName(x.Profile.GameDirectory) == "mac-shaders-1.21.1"
            )
                .Profile;
            if (exclusive)
            {
                var before = Protected(profile.GameDirectory);
                var plan = await api.CallAsync<ShaderEnvironmentPreview>("workspace.browser.irisPreview", profile.Id);
                Check(plan.CanApply && plan.Remove.Length == 3, "real-exclusive-iris-sodium-fabric-api-plan");
                var removed = await api.CallAsync<ShaderEnvironmentResult>(
                    "workspace.browser.irisUninstall",
                    profile.Id,
                    new
                    {
                        fingerprint = plan.Fingerprint
                    }
                );
                Check(
                    removed.Removed.Length == 3 && Directory.GetFiles(
                        Path.Combine(profile.GameDirectory, "mods"),
                        "*.jar"
                    )
                        .Length == 0,
                    "real-exclusive-environment-components-removed"
                );
                Check(
                    before.SequenceEqual(
                        Protected(profile.GameDirectory)
                    ),
                    "exclusive-uninstall-preserves-config-shader-world-bytes"
                );
                Check(
                    File.Exists(
                        Path.Combine(removed.BackupDirectory, "completed.json")
                    ),
                    "exclusive-durable-completion-and-backup"
                );
            }
            else
            {
                var mods = Directory.GetFiles(
                    Path.Combine(profile.GameDirectory, "mods"),
                    "*.jar"
                );
                Check(
                    !mods.Any(
                        x => Path.GetFileName(x)
                            .StartsWith("iris")
                    ) && mods.Any(
                        x => x.Contains("sodium-extra")
                    ) && mods.Any(
                        x => x.Contains("sodium-fabric")
                    ),
                    "real-ui-result-retains-consumer-and-shared-sodium"
                );
            }

            // 卸载后的兼容性只验证启动和实际渲染；保存/重进另由已确认的光影世界两进程记录证明。
            await api.CallAsync<bool>(
                "workspace.instances.saveSettings",
                profile.Id,
                new
                {
                    value = new LaunchSettings
                    {
                        QuickPlayWorld = "",
                        MaximumMemoryMb = 3072
                    },
                    fields = new[]
                    {
                        "QuickPlayWorld",
                        "MaximumMemoryMb"
                    }
                }
            );
            var account = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL036_Remove"
                }
            );
            var accepted = await session.Launches.AcceptAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    account.Id,
                    profile.Id
                )
            );
            var result = await session.Launches.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(3)
                );
            Check(result.State == "succeeded", "post-uninstall-launch-" + result.ErrorCode);
            active = result.Result!.GameProcessId;
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
                if ((log.Contains("Created:") && log.Contains("atlas")) || (await session.Launches.GetGameAsync(active)).State == "exited")
                {
                    break;
                }

                await Task.Delay(500);
            }

            Check(
                log.Contains("Created:") && log.Contains("atlas"),
                "post-uninstall-real-renderer-initialized"
            );
            Check(
                !log.Contains("Using shaderpack:") && !log.Contains("Mod resolution encountered"),
                "iris-absent-no-shader-or-dependency-error"
            );
            var blocked = false;
            try
            {
                await api.CallAsync<ShaderEnvironmentResult>(
                    "workspace.browser.irisUninstall",
                    profile.Id,
                    new
                    {
                        fingerprint = "running"
                    }
                );
            }
            catch (Exception ex)when (ex.Message.Contains("HOST_WRITER_ACTIVE"))
            {
                blocked = true;
            }

            Check(blocked, "running-game-blocks-environment-uninstall");
            await Task.Delay(3000);
            Check(
                (await session.Launches.GetGameAsync(active)).State != "exited",
                "post-uninstall-game-remains-running"
            );
            await session.Launches.StopGameAsync(active);
            deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await session.Launches.GetGameAsync(active)).State != "exited" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            Check(
                (await session.Launches.GetGameAsync(active)).State == "exited",
                "owned-test-process-cleaned-up"
            );
            active = null;
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        exclusive,
                        details.Pid,
                        details.LogFile,
                        profile.Id
                    }
                )
            );
            await session.ShutdownAsync();
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
                        error = ex.ToString()
                    }
                )
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
