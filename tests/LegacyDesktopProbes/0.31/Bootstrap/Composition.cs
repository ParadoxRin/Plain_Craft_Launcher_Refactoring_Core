using System.Text.Json;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Application.Installation;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Adapters.Processes;

namespace Pcl.Desktop.Bootstrap;
/// <summary>唯一组合根：取得数据目录写入权，接线安装入口、凭据与进程 Adapter，并建立单一资源所有权。</summary>
internal static class Composition
{
    internal static DesktopServices Create(AppPaths paths)
    {
        var lease = new FileStream(
            Path.Combine(paths.Root, ".launcher.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        MinecraftService? source = null;
        Pcl.Core.Adapters.Operations.FileOperationStore? operationStore = null;
        Pcl.Core.Adapters.Operations.FileOperationStore? launchStore = null;
        Pcl.Core.Application.Launch.ExistingGameLaunchExecutor? gameExecutor = null;
        var games = new SystemGameProcessHost();
        try
        {
            source = new MinecraftService(
                paths,
                commands: new SystemShellCommandRunner(),
                gameProcesses: games
            );
            IAccountSecrets secrets = new KeychainAccountSecrets(paths.Root);
            var accounts = new AccountManager(paths, secrets);
            var operations = new InstallOperations(paths);
            operations.MarkInterrupted();
            var journal = new LifecycleJournal(paths);
            operationStore = new(paths.Root);
            var target = new InstallationTarget(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion);
            launchStore = new(paths.Root, "launch-operations-v1");
            gameExecutor = new(source, accounts);
            var lane = new SemaphoreSlim(1, 1);
            var operationJournal = new Pcl.Core.Adapters.Diagnostics.BoundedOperationJournal(paths.Root);
            var runner = new Pcl.Core.Application.Operations.InstallOperationService(
                operationStore,
                new Pcl.Core.Adapters.Operations.ExistingVersionInstaller(source),
                target,
                lane,
                () => gameExecutor.HasRunning,
                operationJournal
            );
            var launches = new Pcl.Core.Application.Operations.GameLaunchOperationService(launchStore, gameExecutor, lane, operationJournal);
            var installs = new VersionInstallService(runner, target, journal.Record);
            var lifetime = new InstallationLifetime(
                installs,
                [
                    ("operation-store", operationStore),
                    ("launch-store", launchStore),
                    ("game-executor", gameExecutor),
                    ("minecraft", source),
                    ("game-processes", games),
                    ("root-lock", new DataDirectoryLease(lease, journal))
                ],
                journal.Record,
                launches.StopAsync
            );
            journal.Record("scope.created");
            return new(
                source,
                accounts,
                installs,
                launches,
                gameExecutor.BorrowLocalSession,
                new(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion),
                lifetime,
                journal
            );
        }
        catch
        {
            try
            {
                source?.Dispose();
            }
            finally
            {
                gameExecutor?.Dispose();
                launchStore?.Dispose();
                operationStore?.Dispose();
                games.Dispose();
                lease.Dispose();
            }

            throw;
        }
    }
}

// 仅 App/Bootstrap 持有实现；展示层接收 Contract 接口和未迁移页面借用的旧核心服务。
internal sealed record DesktopServices(
    MinecraftService LegacyService,
    AccountManager Accounts,
    IVersionInstallService Installation,
    Pcl.Core.Contracts.Launch.IGameLaunchOperations GameLaunch,
    Func<string, Pcl.Core.Contracts.Launch.IGameSession?> BorrowGame,
    InstallationTarget Target,
    IDesktopLifetime Lifetime,
    LifecycleJournal Journal,
    Pcl.Client.LauncherSession? Remote = null
);
/// <summary>只写固定生命周期事件，用于排查释放顺序；无账户、请求参数或凭据。</summary>
internal sealed class LifecycleJournal(AppPaths paths)
{
    private readonly object gate = new();
    private bool sealedJournal;
    internal void Record(string name)
    {
        lock (gate)
        {
            if (sealedJournal)
            {
                return;
            }

            try
            {
                var file = Path.Combine(paths.Evidence, "phase-one-lifecycle.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length > 1_048_576)
                {
                    File.Move(file, file + ".previous", true);
                }

                File.AppendAllText(
                    file,
                    JsonSerializer.Serialize(
                        new
                        {
                            time = DateTimeOffset.UtcNow,
                            name
                        }
                    ) + "\n"
                );
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // 最后一条磁盘事件在释放锁之前写入；之后的诊断不得再写这个数据目录。
    internal void ReleaseLease(FileStream lease)
    {
        lock (gate)
        {
            Record("root-lock.releasing");
            sealedJournal = true;
            lease.Dispose();
        }
    }
}

internal sealed class DataDirectoryLease(FileStream lease, LifecycleJournal journal) : IDisposable
{
    public void Dispose() => journal.ReleaseLease(lease);
}
