using System.Collections.Concurrent;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Application.Launch;
// 应用层启动编排：客户端只选账户/实例；凭据和进程通过注入的旧核心边界调用，不由平台 Adapter 决定授权政策。
public sealed class ExistingGameLaunchExecutor(MinecraftService source, AccountManager accounts, string? javaOverride = null) : IGameLaunchExecutor, IDisposable
{
    private readonly ConcurrentDictionary<string, RunningGame> games = new();
    private readonly ConcurrentDictionary<string, (Pcl.Core.Ports.Processes.GameProcessHandle Handle, string Instance)> detached = new();
    private readonly ConcurrentDictionary<string, string> instances = new();
    private readonly LaunchHookRunner hooks = new(source.Commands);
    private readonly LaunchGuidanceService guidance = new(source.Paths, accounts);
    public Pcl.Core.Contracts.Launch.LaunchGuidance GetGuidance(string accountId, string culture) => guidance.Get(accountId, culture);
    public bool HasManagedRunning => games.Values.Any(
        x => !x.Completion.IsCompleted
    );

    public Task WaitForManagedGamesAsync() => Task.WhenAll(
        games.Values.Select(
            x => x.Completion
        )
    );
    public bool HasRunning => games.Values.Any(
        x => !x.Completion.IsCompleted
    ) || detached.Values.Any(
        x => source.GameProcesses.IsRunning(x.Handle)
    );

    public Pcl.Core.Contracts.Accounts.GameDetails Details(string id)
    {
        if (games.TryGetValue(id, out var game))
        {
            return new(id, game.Pid, false, game.LogFile);
        }

        if (detached.TryGetValue(id, out var item))
        {
            var receipt = JsonFiles.Read(
                Path.Combine(source.Paths.Evidence, "detached-launch.json")
            );
            return new(id, item.Handle.Pid, true, (string)receipt["log"]!);
        }

        throw new OperationInstallException("GAME_NOT_MANAGED_BY_THIS_HOST");
    }

    public RunningGame? BorrowLocalSession(string id) => games.GetValueOrDefault(id);
    public async Task<StartedGameProcess> StartAsync(GameLaunchInput input, Action beforeSpawn, CancellationToken ct)
    {
        if (HasRunning)
        {
            throw new OperationInstallException("GAME_ALREADY_RUNNING");
        }

        var trial = guidance.Require(input.AccountId, input.Culture, input.SponsorRead, input.PurchaseRead, input.Demo);
        var account = trial
            ? null
            : accounts.Accounts.SingleOrDefault(
                x => x.Id == input.AccountId
            ) ?? (trial
                ? null
                : throw new LoginRequiredException("账户不存在"));
        if (account?.Kind == AccountKind.External)
        {
            throw new LoginRequiredException("此外置账户暂不支持启动");
        }

        using var auth = new MicrosoftAuthentication();
        var session = trial
            ? GameSession.Trial
            : await accounts.ResolveAsync(account!.Id, auth, ct)
                .ConfigureAwait(false);
        GameLaunchAuthorization.Require(session);
        LaunchGuidanceService.RecordVerified(source.Paths, session);
        var profile = InstanceRegistry.List(source, true)
            .SingleOrDefault(
                x => x.Id == input.InstanceId
            ) ?? throw new InvalidDataException("实例不存在");
        var version = InstanceRegistry.Load(source, profile);
        var java = javaOverride is not null
            ? await JavaService.InspectAsync(javaOverride, ct, source.Commands)
            : await new LauncherSettingsStore(source.Paths).ResolveJavaAsync(source, version, profile.GameDirectory, ct: ct)
                .ConfigureAwait(false);
        new OfflineSkinService(source.Paths).Prepare(account, version, profile.GameDirectory, profile.GameVersion, ct);
        new AppearanceService(source.Paths).PrepareGameLanguage(profile.GameDirectory, profile.GameVersion);
        var launcher = new LaunchService(source);
        var plan = await launcher.PrepareAsync(version, java, session, account?.ClientId ?? "", profile.GameDirectory, ct)
            .ConfigureAwait(false);
        await hooks.RunAsync(
            LaunchHookSettings.Load(source.Paths),
            profile.GameDirectory,
            ct: ct
        )
            .ConfigureAwait(false);
        beforeSpawn();
        if (input.Detached)
        {
            var handle = await launcher.StartDetachedAsync(plan, session, ct)
                .ConfigureAwait(false);
            detached[handle.Id] = (handle, input.InstanceId);
            return new(handle.Id, handle.Pid, true);
        }

        var game = await launcher.StartAsync(plan, session, ct: ct)
            .ConfigureAwait(false);
        games[game.Id] = game;
        instances[game.Id] = input.InstanceId;
        return new(game.Id, game.Pid, false);
    }

    public ManagedGameState Get(string id)
    {
        if (games.TryGetValue(id, out var game))
        {
            return new(
                id,
                !game.Completion.IsCompleted,
                game.Completion.IsCompletedSuccessfully
                    ? game.Completion.Result
                    : null,
                false
            );
        }

        if (detached.TryGetValue(id, out var independent))
        {
            var running = source.GameProcesses.IsRunning(independent.Handle);
            var completion = source.GameProcesses.WaitForExitAsync(independent.Handle);
            var exit = completion.IsCompletedSuccessfully
                ? (int? )completion.Result
                : null;
            return new(id, running, exit, true);
        }

        throw new OperationInstallException("GAME_NOT_MANAGED_BY_THIS_HOST");
    }

    public Task StopAsync(string id, CancellationToken ct)
    {
        if (games.TryGetValue(id, out var game))
        {
            return game.StopAsync(ct);
        }

        if (detached.TryGetValue(id, out var independent))
        {
            return source.GameProcesses.TerminateAsync(independent.Handle, ct: ct);
        }

        throw new OperationInstallException("GAME_NOT_MANAGED_BY_THIS_HOST");
    }

    public void Dispose() => hooks.Dispose();
}
