using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Workspaces;

/// <summary>最终 Host 包驱动真实 Iris/光影配置与包卸载，并启动隔离游戏；不读取或输出账号令牌。</summary>
internal static class Shader035Flow
{
    public static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        var checks = new List<string>();
        var evidence = new List<object>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        string Hash(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(
                SHA256.HashData(input)
            );
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")!;
        await using var session = new LauncherSession(
            new HostClient(native, "", root, reconnectable: true)
        );
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var instances = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var profile = instances.Items.Single(
                x => Path.GetFileName(x.Profile.GameDirectory) == "mac-shaders-1.21.1"
            )
                .Profile;
            var config = Path.Combine(profile.GameDirectory, "config/iris.properties");
            // 修改仅发生在显式 0.35 验收副本，不触碰原光影存档和配置。
            Check(
                root.Contains("verification-0.35"),
                "isolated-data-root"
            );
            File.WriteAllText(config, "enableShaders=false\nshadowDistance=16\n");
            var baseline = File.ReadAllBytes(config);
            var managed = await api.CallAsync<Pcl.Core.ManagedResource[]>("workspace.browser.list", profile.Id);
            var shader = managed.Single(
                x => x.Kind == Pcl.Core.ResourceKind.Shader
            );
            var package = Path.Combine(profile.GameDirectory, "shaderpacks", shader.FileName);
            var packageHash = Hash(package);
            var iris = Directory.GetFiles(
                Path.Combine(profile.GameDirectory, "mods"),
                "iris-*.jar"
            )
                .Single();
            var irisHash = Hash(iris);
            var user = Path.Combine(profile.GameDirectory, "shaderpacks/user-keep.txt");
            File.WriteAllText(user, "user-created data");
            var backend = await api.CallAsync<string[]>("workspace.browser.backends", profile.Id);
            Check(
                backend.Any(
                    x => x.Contains("iris") && x.Contains("1.8.8") && x.Contains("1.21.1") && x.Contains("fabric")
                ),
                "actual-iris-version-and-loader-detected"
            );
            await api.CallAsync<bool>(
                "workspace.browser.activate",
                profile.Id,
                new
                {
                    projectId = shader.ProjectId
                }
            );
            Check(
                File.ReadAllText(config)
                    .Contains(shader.FileName),
                "real-shader-selection-applied-through-host"
            );
            await api.CallAsync<bool>("workspace.browser.undo", profile.Id);
            Check(
                baseline.SequenceEqual(
                    File.ReadAllBytes(config)
                ),
                "real-shader-config-undo-byte-identical"
            );
            Check(
                Hash(package) == packageHash && Hash(iris) == irisHash,
                "undo-preserves-shader-package-and-iris"
            );
            var account = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL035_Shader"
                }
            );
            // 只验证实际渲染与运行保护；世界视觉验收单独列出，不由日志替代。
            async Task Launch(string name)
            {
                var accepted = await session.Launches.AcceptAsync(
                    new GameLaunchCommand(
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
                Check(result.State == "succeeded", name + "-launch-" + result.ErrorCode);
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
                    var until = DateTime.UtcNow.AddMinutes(2);
                    string log = "";
                    while (DateTime.UtcNow < until)
                    {
                        log = File.Exists(details.LogFile)
                            ? File.ReadAllText(details.LogFile)
                            : "";
                        if ((log.Contains("Created:") && log.Contains("atlas")) || (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                        {
                            break;
                        }

                        await Task.Delay(300);
                    }

                    Check(
                        log.Contains("Created:") && log.Contains("atlas"),
                        name + "-real-minecraft-renderer-initialized"
                    );
                    bool blocked = false;
                    try
                    {
                        await api.CallAsync<bool>("workspace.browser.undo", profile.Id);
                    }
                    catch (Exception ex)when (ex.Message.Contains("HOST_WRITER_ACTIVE"))
                    {
                        blocked = true;
                    }

                    Check(blocked, name + "-running-game-blocks-resource-mutation");
                    await Task.Delay(3000);
                    Check(
                        (await session.Launches.GetGameAsync(game.GameProcessId)).State != "exited",
                        name + "-alive-after-render"
                    );
                    evidence.Add(
                        new
                        {
                            name,
                            game.Pid,
                            log = details.LogFile,
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

            await Launch("after-config-undo");
            await api.CallAsync<bool>(
                "workspace.browser.activate",
                profile.Id,
                new
                {
                    projectId = shader.ProjectId
                }
            );
            await Launch("shader-selected");
            var afterGame = File.ReadAllBytes(config);
            bool conflict = false;
            try
            {
                await api.CallAsync<bool>("workspace.browser.undo", profile.Id);
            }
            catch
            {
                conflict = true;
            }

            if (conflict)
            {
                Check(
                    afterGame.SequenceEqual(
                        File.ReadAllBytes(config)
                    ),
                    "iris-rewritten-config-is-not-overwritten-by-undo"
                );
            }
            else
            {
                Check(true, "iris-left-config-unchanged-and-undo-succeeded");
            }

            // 包卸载不负责恢复选择；在隔离验收中将配置恢复为关闭光影后验证卸载。
            File.WriteAllBytes(config, baseline);
            await api.CallAsync<bool>(
                "workspace.browser.remove",
                profile.Id,
                new
                {
                    projectId = shader.ProjectId
                }
            );
            Check(
                !File.Exists(package),
                "owned-real-shader-package-uninstalled"
            );
            Check(
                Hash(iris) == irisHash && File.ReadAllText(user) == "user-created data",
                "uninstall-retains-iris-and-user-added-file"
            );
            Check(
                Directory.GetFiles(
                    Path.Combine(profile.GameDirectory, ".pcl-backups"),
                    "0.zip",
                    SearchOption.AllDirectories
                )
                    .Any(
                        x => Hash(x) == packageHash
                    ),
                "uninstall-retains-verified-shader-backup"
            );
            await Launch("after-package-uninstall");
            // 有效但与当前配置冲突的持久化意图必须让真实 game.launch 失败，不能只在单元入口拒绝。
            var interrupted = Guid.NewGuid()
                .ToString("N");
            var backupDirectory = Path.Combine(profile.GameDirectory, ".pcl-backups", "activation-" + interrupted);
            Directory.CreateDirectory(backupDirectory);
            File.WriteAllText(
                Path.Combine(backupDirectory, "previous.config"),
                "before"
            );
            File.WriteAllText(
                Path.Combine(backupDirectory, "applied.config"),
                "after"
            );
            var journal = Path.Combine(profile.GameDirectory, ".pcl-activation-transaction.json");
            File.WriteAllText(
                journal,
                JsonSerializer.Serialize(
                    new
                    {
                        format = 1,
                        kind = "apply",
                        operationId = interrupted,
                        activation = new
                        {
                            format = 2,
                            id = interrupted,
                            target = "config/iris.properties",
                            hadFile = true,
                            beforeSha256 = Hash(
                                Path.Combine(backupDirectory, "previous.config")
                            )
                                .ToLowerInvariant(),
                            appliedSha256 = Hash(
                                Path.Combine(backupDirectory, "applied.config")
                            )
                                .ToLowerInvariant()
                        }
                    }
                )
            );
            var beforeBlocked = File.ReadAllBytes(config);
            var blockedAdmission = await session.Launches.AcceptAsync(
                new GameLaunchCommand(
                    Guid.NewGuid()
                        .ToString("N"),
                    account.Id,
                    profile.Id
                )
            );
            var blockedLaunch = await session.Launches.WaitAsync(blockedAdmission.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(1)
                );
            Check(
                blockedLaunch.State == "failed" && blockedLaunch.Result is null,
                "unresolved-config-transaction-blocks-real-host-game-launch"
            );
            Check(
                beforeBlocked.SequenceEqual(
                    File.ReadAllBytes(config)
                ),
                "blocked-host-launch-retains-external-config"
            );
            File.Delete(journal);
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
