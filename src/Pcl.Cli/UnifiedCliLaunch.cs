using Pcl.Core.Application.Launch;
using Pcl.Core;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Application.Operations;

namespace Pcl.Cli;
// CLI 只完成用户选账户/交互登录；刷新、Java、皮肤和进程创建属于可查询的 game.launch 操作。
internal static class UnifiedCliLaunch
{
    internal static async Task<int> RunAsync(
        MinecraftService source,
        AccountManager accounts,
        GameProfile profile,
        string? offlineName,
        string? accountId,
        string? javaOverride,
        string key,
        bool remember,
        bool detached,
        int smokeSeconds,
        CancellationToken ct
    )
    {
        if (offlineName is not null)
        {
            accountId = accounts.AddOffline(offlineName)
                .Id;
        }

        if (accountId is null)
        {
            using var auth = new MicrosoftAuthentication();
            var clientId = MicrosoftApplication.BuiltIn.RequireClientId();
            var challenge = await auth.BeginAsync(clientId, ct);
            Console.WriteLine($"请在浏览器打开 {challenge.VerificationUri} 并输入 {challenge.UserCode}。");
            var login = await auth.CompleteLoginAsync(clientId, challenge, ct);
            accountId = accounts.SaveMicrosoft(login, remember)
                .Id;
        }

        var account = accounts.Accounts.Single(
            x => x.Id == accountId
        );
        using var store = new FileOperationStore(source.Paths.Root, "launch-operations-v1");
        using var executor = new ExistingGameLaunchExecutor(source, accounts, javaOverride);
        var revision = javaOverride is null
            ? null
            : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        Path.GetFullPath(javaOverride)
                    )
                )
            );
        var launches = new GameLaunchOperationService(store, executor, inputRevision: revision);
        try
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrWhiteSpace(culture))
            {
                culture = "en-US";
            }
            var consent = await ConfirmGuidanceAsync(executor.GetGuidance(accountId, culture), culture, ct);
            if (consent is null)
            {
                return 1;
            }
            var accepted = await launches.AcceptAsync(
                new(key, accountId, profile.Id, detached, consent),
                ct
            );
            Console.WriteLine("LAUNCH OPERATION " + accepted.OperationId);
            var waiting = launches.WaitAsync(accepted.OperationId);
            bool sent = false;
            while (!waiting.IsCompleted)
            {
                if (ct.IsCancellationRequested && !sent)
                {
                    await launches.CancelAsync(accepted.OperationId);
                    sent = true;
                }

                await Task.WhenAny(
                    waiting,
                    Task.Delay(100)
                );
            }

            var done = await waiting;
            if (done.Result is null)
            {
                throw new InvalidOperationException(done.ErrorCode ?? "LAUNCH_FAILED");
            }

            if (accepted.Replayed)
            {
                Console.WriteLine("已返回原启动结果；本次不创建或接管旧游戏进程。");
                return 0;
            }

            if (detached)
            {
                Console.WriteLine("DETACHED GAME PID " + done.Result.Pid);
                return 0;
            }

            var game = executor.BorrowLocalSession(done.Result.GameProcessId) ?? throw new InvalidOperationException("GAME_NOT_MANAGED_BY_THIS_HOST");
            game.Output += Console.WriteLine;
            Console.WriteLine($"GAME PID {game.Pid}; LOG {game.LogFile}");
            try
            {
                if (smokeSeconds > 0)
                {
                    await Task.WhenAny(
                        game.Completion,
                        Task.Delay(
                            TimeSpan.FromSeconds(smokeSeconds),
                            ct
                        )
                    );
                    var alive = !game.HasExited;
                    var text = File.ReadAllText(game.LogFile);
                    bool renderer = text.Contains("OpenGL", StringComparison.OrdinalIgnoreCase) || text.Contains("Backend library", StringComparison.OrdinalIgnoreCase) || text.Contains("LWJGL Version:", StringComparison.OrdinalIgnoreCase);
                    bool atlas = text.Contains("Created:") && text.Contains("atlas");
                    if (alive)
                    {
                        await game.StopAsync();
                    }

                    var exit = await game.Completion;
                    var version = InstanceRegistry.Load(source, profile);
                    var result = new
                    {
                        status = renderer && atlas
                            ? "render_initialization_observed"
                            : "needs_review",
                        profileId = profile.Id,
                        gameVersion = profile.GameVersion,
                        loader = profile.Loader,
                        javaMajor = version.JavaMajor,
                        accountKind = account.Kind.ToString()
                            .ToLowerInvariant(),
                        graphicsLogObserved = renderer,
                        atlasCreationObserved = atlas,
                        stoppedBySmokeTest = alive,
                        exitCode = exit,
                        operationId = accepted.OperationId,
                        visualInspection = "not_performed",
                        interactiveWorldPlay = "not_tested"
                    };
                    JsonFiles.Write(
                        AppPaths.SafeChild(source.Paths.Evidence, "launch-result-" + profile.Id + ".json"),
                        result
                    );
                    JsonFiles.Write(
                        Path.Combine(source.Paths.Evidence, "launch-result.json"),
                        result
                    );
                    return renderer && atlas && (alive || exit == 0)
                        ? 0
                        : 2;
                }

                return await game.Completion;
            }
            finally
            {
                game.Output -= Console.WriteLine;
                await game.DisposeAsync();
            }
        }
        finally
        {
            await launches.StopAsync();
        }
    }
    // CLI owns text interaction only; Host/core decides the same guidance policy as Desktop.
    internal static async Task<Pcl.Core.Contracts.Launch.LaunchGuidanceConsent?> ConfirmGuidanceAsync(Pcl.Core.Contracts.Launch.LaunchGuidance guide, string culture, CancellationToken ct)
    {
        Console.WriteLine("PCL 原作者：龙腾猫跃。请考虑自愿支持原作者：" + guide.SponsorUrl);
        if (guide.ShowPurchase)
        {
            Console.WriteLine("请购买正版 Minecraft 支持开发者：" + guide.PurchaseUrl);
        }
        Console.WriteLine(guide.RequireDemo ? "尚未验证正版。输入 demo 启动官方试玩，其他输入取消。" : "阅读提示后输入 continue 继续、demo 试玩，其他输入取消。");
        var input = await Console.In.ReadLineAsync(ct);
        var demo = input == "demo";
        if (!demo && (guide.RequireDemo || input != "continue"))
        {
            return null;
        }
        return new(culture, true, guide.ShowPurchase, demo);
    }

}
