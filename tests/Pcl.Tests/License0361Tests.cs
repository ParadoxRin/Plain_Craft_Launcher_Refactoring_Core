using Pcl.Core.Adapters.Operations;
using Pcl.Core.Application.Operations;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.Core;
using Pcl.Core.Adapters.Appearance;
using Pcl.Core.Application.Launch;
using Pcl.Core.Ports.Operations;
using Pcl.Domain;

internal static class License0361Tests
{
    // Real file state plus the execution boundary; no OAuth traffic or personal credentials.
    public static async Task<int> Run(string root)
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }
            checks.Add(name);
        }
        var paths = new AppPaths(Path.GetFullPath(root));
        var accounts = new AccountManager(paths, new NoSecrets());
        var offline = accounts.AddOffline("License_Test");
        var guidance = new LaunchGuidanceService(paths, accounts);
        var zh = guidance.Get(offline.Id, "zh-CN");
        Check(zh.ShowSponsor && zh.ShowPurchase && !zh.RequireDemo, "chinese-purchase-and-sponsor-guidance");
        var en = guidance.Get(offline.Id, "en-US");
        Check(en.RequireDemo, "unverified-non-chinese-trial-required");
        foreach (var flags in new[] { (false, false, false), (true, false, false), (false, true, true), (true, true, false) })
        {
            try
            {
                guidance.Require(offline.Id, "en-US", flags.Item1, flags.Item2, flags.Item3);
                throw new Exception("Invalid guidance accepted");
            }
            catch (OperationInstallException ex)
            {
                Check(ex.Code == "LAUNCH_GUIDANCE_REQUIRED", "reject-incomplete-consent-" + checks.Count);
            }
        }
        Check(guidance.Require(offline.Id, "en-US", true, true, true), "trial-consent-accepted");
        Check(!guidance.Require(offline.Id, "zh-CN", true, true, false), "chinese-offline-preserved-after-guidance");
        GameLaunchAuthorization.Require(GameSession.Trial);
        Check(true, "official-trial-identity-authorized");
        try
        {
            GameLaunchAuthorization.Require(GameSession.Trial with { Name = "Tampered" });
            throw new Exception("Tampered trial accepted");
        }
        catch (LoginRequiredException)
        {
            Check(true, "tampered-trial-rejected");
        }
        LaunchGuidanceService.RecordVerified(paths, AccountManager.OfflineSession("License_Test"));
        Check(!File.Exists(Path.Combine(paths.Root, "launch-guidance.json")), "offline-never-records-purchase");
        Check(LaunchGuidancePolicy.Evaluate(true, true, false) == (false, false), "prior-verification-removes-purchase-and-trial-hint");
        var appearance = new AppearanceSettings { Accent = "#FF0000", AccentMode = "rainbow", TitleMode = "image" };
        var windows = PlatformAppearance.Normalize(appearance, true);
        Check(windows.Accent == "#0875D1" && windows.AccentMode == "static" && windows.TitleMode == "default", "windows-blue-no-dynamic-unlock-like-theme");
        Check(PlatformAppearance.Normalize(appearance, false) == appearance, "mac-decoration-retained");
        using var source = new MinecraftService(paths);
        using var executor = new ExistingGameLaunchExecutor(source, accounts);
        try
        {
            await executor.StartAsync(new(offline.Id, "missing-instance", false), () => throw new Exception("Unexpected process"), CancellationToken.None);
            throw new Exception("Missing guidance accepted");
        }
        catch (OperationInstallException ex)
        {
            Check(ex.Code == "LAUNCH_GUIDANCE_REQUIRED", "executor-blocks-before-spawn-and-java");
        }
        try
        {
            await executor.StartAsync(new(offline.Id, "missing-instance", false, "zh-CN", true, true), () => throw new Exception("Unexpected process"), CancellationToken.None);
            throw new Exception("Missing instance accepted");
        }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains("实例"), "valid-consent-reaches-original-installation-validation");
        }
        // Consent participates in idempotency; persisted results must not respawn a game.
        var operationRoot = Path.Combine(paths.Root, "idempotency");
        var command = new GameLaunchCommand("guidance", offline.Id, "fixture", true, new("zh-CN", true, true));
        string operationId;
        using (var store = new FileOperationStore(operationRoot, "launch-operations-v1"))
        {
            var fake = new FakeExecutor();
            var operations = new GameLaunchOperationService(store, fake);
            var accepted = await operations.AcceptAsync(command);
            operationId = accepted.OperationId;
            var done = await operations.WaitAsync(operationId);
            var replay = await operations.AcceptAsync(command);
            Check(done.State == "succeeded" && replay.Replayed && fake.Starts == 1, "consent-idempotency-spawns-once-fixture");
            Check(fake.Input is { SponsorRead: true, PurchaseRead: true, Demo: false, Detached: true }, "consent-and-detached-forwarded-to-executor");
            try
            {
                await operations.AcceptAsync(command with { Guidance = command.Guidance! with { Demo = true } });
                throw new Exception("Changed consent reused idempotency key");
            }
            catch (OperationRejectedException ex)
            {
                Check(ex.Code == "IDEMPOTENCY_CONFLICT", "changed-consent-conflicts");
            }
            await operations.StopAsync();
        }
        using (var store = new FileOperationStore(operationRoot, "launch-operations-v1"))
        {
            var fake = new FakeExecutor();
            var operations = new GameLaunchOperationService(store, fake);
            var replay = await operations.AcceptAsync(command);
            Check(replay.Replayed && replay.OperationId == operationId && fake.Starts == 0, "restart-preserves-receipt-without-respawn-fixture");
            await operations.StopAsync();
        }
        JsonFiles.Write(Path.Combine(paths.Root, "result.json"), new { status = "passed", checks, windowsRuntimeTest = false, realGame = false });
        foreach (var check in checks)
        {
            Console.WriteLine("PASS " + check);
        }
        return 0;
    }

    private sealed class FakeExecutor : IGameLaunchExecutor
    {
        public int Starts;
        public GameLaunchInput? Input;
        public Task<StartedGameProcess> StartAsync(GameLaunchInput input, Action beforeSpawn, CancellationToken ct)
        {
            Input = input;
            Starts++;
            beforeSpawn();
            return Task.FromResult(new StartedGameProcess("fixture-game", 42, input.Detached));
        }
        public ManagedGameState Get(string id) => new(id, true, null, true);
        public Task StopAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoSecrets : IAccountSecrets
    {
        public string? Get(string id) => throw new Exception("Unexpected keychain access");
        public void Set(string id, string value) => throw new Exception("Unexpected keychain access");
        public void Delete(string id) { }
    }
}
