using Pcl.Core;
using Pcl.Core.Adapters.Accounts;
using Pcl.Core.Adapters.Credentials;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Adapters.Processes;
using Pcl.Core.Adapters.Pages;
using Pcl.Core.Adapters.Launch;
using Pcl.Core.Application.Accounts;
using Pcl.Core.Application.Launch;
using Pcl.Core.Application.Operations;
using Pcl.Core.Application.Pages;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Host;
// 唯一运行时组合根：用例借用执行器，资源按创建顺序的逆序释放；不提供全局 Instance。
internal sealed class HostRuntime : IAsyncDisposable
{
    private readonly List<IDisposable> owned = new();
    private readonly object gate = new();
    private Task? shutdown;
    private Task? disposal;
    internal MinecraftService Source { get; }
    internal AccountManager Accounts { get; }
    internal HostAccountService AccountService { get; }
    internal ExistingGameLaunchExecutor Executor { get; }
    internal InstallOperationService Installs { get; }
    internal PageOperationService Pages { get; }
    internal GameLaunchOperationService Launches { get; }
    internal PageExecutionAdapter PageExecutor { get; }
    internal InstallationTarget Target { get; }
    internal Pcl.Core.Adapters.Diagnostics.BoundedOperationJournal Journal { get; }

    internal HostRuntime(AppPaths paths, OperationEventBroker broker)
    {
        try
        {
            MeloongCore.ConfigProviders.Default = new MeloongCore.JsonConfigProvider(
                Path.Combine(paths.Root, "settings.json")
            );
            var games = Own(
                new SystemGameProcessHost()
            );
            Source = Own(
                new MinecraftService(
                    paths,
                    commands: new SystemShellCommandRunner(),
                    gameProcesses: games
                )
            );
            var installStore = Own(
                new FileOperationStore(paths.Root)
            );
            var accountStore = Own(
                new FileOperationStore(paths.Root, "account-operations-v1")
            );
            var launchStore = Own(
                new FileOperationStore(paths.Root, "launch-operations-v1")
            );
            Accounts = new AccountManager(
                paths,
                PlatformAccountSecrets.Create(paths.Root)
            );
            AccountService = new HostAccountService(
                new AccountBackend(Accounts),
                accountStore
            );
            AccountService.Restore();
            Executor = Own(
                new ExistingGameLaunchExecutor(
                    new GameLaunchBackend(Source, Accounts),
                    new LaunchGuidanceService(
                        new LaunchGuidanceState(paths, Accounts)
                    )
                )
            );
            var lane = Own(
                new SemaphoreSlim(1, 1)
            );
            Target = new(Source.Platform.Os, Source.Platform.Architecture, Source.Platform.OsVersion);
            Journal = new Pcl.Core.Adapters.Diagnostics.BoundedOperationJournal(paths.Root);
            Installs = new InstallOperationService(
                installStore,
                new ExistingVersionInstaller(Source),
                Target,
                lane,
                () => Executor.HasRunning,
                Journal,
                broker
            );
            var pageStore = Own(
                new FileOperationStore(paths.Root, "page-operations-v1")
            );
            var privateStore = Own(
                new FileOperationStore(paths.Root, "page-execution-v1")
            );
            PageExecutor = Own(
                new PageExecutionAdapter(Source, privateStore, Accounts)
            );
            Pages = new PageOperationService(pageStore, PageExecutor, lane, broker);
            Launches = new GameLaunchOperationService(launchStore, Executor, lane, Journal, observer: broker);
            new InstallOperations(paths).MarkInterrupted();
        }
        catch (Exception constructionError)
        {
            // 构造失败保留原错误，并清理已经创建的资源；清理错误不能覆盖根因。
            try
            {
                ReleaseOwned();
            }
            catch (Exception releaseError)
            {
                throw new AggregateException(constructionError, releaseError);
            }

            throw;
        }
    }

    private T Own<T>(T value)
    where T : IDisposable
    {
        owned.Add(value);
        return value;
    }

    internal Task ShutdownAsync(bool requested)
    {
        lock (gate)
        {
            return shutdown ??= DrainAsync(requested);
        }
    }

    private async Task DrainAsync(bool requested)
    {
        var accountStop = AccountService.StopAsync();
        if (requested)
        {
            await Task.WhenAll(
                accountStop,
                Installs.StopAsync(),
                Launches.StopAsync(),
                Pages.StopAsync()
            );
        }
        else
        {
            // 连接断开不等于取消；托管游戏排空，独立游戏不被根作用域释放所终止。
            await Task.WhenAll(
                accountStop,
                Installs.DrainAsync(),
                Launches.DrainAsync(),
                Pages.DrainAsync()
            );
            await Executor.WaitForManagedGamesAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            return new(
                disposal ??= DisposeOwnedAsync()
            );
        }
    }

    private async Task DisposeOwnedAsync()
    {
        await ShutdownAsync(true);
        ReleaseOwned();
    }

    private void ReleaseOwned()
    {
        List<Exception> errors = [];
        foreach (var item in owned.AsEnumerable()
            .Reverse())
        {
            try
            {
                item.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        owned.Clear();
        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }
}
