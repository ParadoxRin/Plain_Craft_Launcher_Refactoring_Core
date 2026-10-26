using System.Text.Json;
using Pcl.Desktop;
using Pcl.Core.Application.Accounts;
using Pcl.Core.Application.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Accounts;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Ports.Launch;
using Pcl.Domain;

var results = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception(name);
    }

    results.Add(name);
    Console.WriteLine(name);
}

// 普通关闭只等待视图，不取消已经受理的任务；退出可以升级为取消且只清理一次。
var lifetime = new ViewLifetime();
var cancellations = 0;
var cleaned = 0;
var work = lifetime.Begin(
    () => cancellations++
);
lifetime.OnStop(
    () =>
    {
        cleaned++;
        return Task.CompletedTask;
    }
);
var close = lifetime.StopAsync(false);
Check(
    !close.IsCompleted && cancellations == 0 && cleaned == 0,
    "close-waits-without-implicit-cancel"
);
Check(
    ReferenceEquals(
        close,
        lifetime.StopAsync(true)
    ) && cancellations == 1,
    "exit-upgrades-existing-close"
);
var repeatedClose = lifetime.StopAsync(true);
Check(
    ReferenceEquals(close, repeatedClose) && cancellations == 1,
    "repeat-exit-does-not-repeat-cancellation"
);
var rejected = false;
try
{
    lifetime.Begin();
}
catch (OperationCanceledException)
{
    rejected = true;
}

Check(rejected, "closing-rejects-new-activities");
work.Dispose();
work.Dispose();
await close;
await lifetime.StopAsync(false);
Check(cleaned == 1, "activity-and-cleanup-single-owner");
var failure = new ViewLifetime();
var secondCleanup = false;
failure.OnStop(
    () => throw new IOException("fixture")
);
failure.OnStop(
    () =>
    {
        secondCleanup = true;
        return Task.CompletedTask;
    }
);
try
{
    await failure.StopAsync(false);
}
catch (AggregateException)
{
}

Check(secondCleanup, "cleanup-failure-does-not-skip-other-resources");
// Application 真实状态机 + 替身技术边界；挑战提示和令牌不得进入 receipt。
var backend = new FakeAccountBackend();
var store = new MemoryStore();
var accounts = new HostAccountService(backend, store);
accounts.Restore();
var accepted = accounts.Begin("account-test", true);
await backend.Challenged.Task.WaitAsync(
    TimeSpan.FromSeconds(5)
);
Check(
    accounts.Get(accepted.OperationId)
        .Prompt?.UserCode == "temporary-code",
    "account-challenge-visible-in-memory"
);
Check(
    store.Items.Values.All(
        b => !System.Text.Encoding.UTF8.GetString(b)
            .Contains("temporary-code")
    ),
    "account-receipts-redact-challenge"
);
Check(
    accounts.Begin("account-test", true)
        .OperationId == accepted.OperationId && backend.Logins == 1,
    "account-idempotent-acceptance"
);
try
{
    accounts.Begin("account-test", false);
    throw new Exception("missing conflict");
}
catch (OperationRejectedException ex)when (ex.Code == "IDEMPOTENCY_CONFLICT")
{
    Check(true, "account-idempotency-conflict");
}

await accounts.StopAsync();
await accounts.StopAsync();
Check(
    accounts.Get(accepted.OperationId)
        .State == "cancelled",
    "account-stop-observes-terminal-state"
);
Check(
    accounts.Snapshot()
        .Accounts.Single()
        .Name == "Fixture",
    "account-index-contract-mapping"
);
var appearance = (Pcl.Core.Contracts.Accounts.AccountAppearance)await accounts.AppearanceAsync("fixture", "get", null, null, false);
Check(
    appearance.Skins.Single()
        .Variant == "slim",
    "skin-contract-mapping-preserved"
);
accounts.Logout("fixture", true);
Check(backend.Removed, "account-remove-delegated-once");
// 启动策略先于执行边界；同一执行器重复释放不会释放进程适配器两次。
var games = new FakeGameBackend();
var guidance = new LaunchGuidanceService(
    new GuidanceState()
);
var executor = new ExistingGameLaunchExecutor(games, guidance);
try
{
    await executor.StartAsync(
        new("fixture", "instance", false),
        () =>
        {
        },
        default
    );
    throw new Exception("guidance bypassed");
}
catch (OperationInstallException ex)when (ex.Code == "LAUNCH_GUIDANCE_REQUIRED")
{
    Check(games.Starts == 0, "guidance-before-physical-start");
}

var beforeSpawn = 0;
var started = await executor.StartAsync(
    new("fixture", "instance", true, SponsorRead: true),
    () => beforeSpawn++,
    default
);
Check(
    started.Detached && beforeSpawn == 1 && games.Starts == 1,
    "detached-start-and-commit-boundary-preserved"
);
executor.Dispose();
executor.Dispose();
Check(games.Disposals == 1, "launch-backend-single-release");
Check(
    typeof(HostAccountService).Assembly.GetName()
        .Name == "Pcl.Application" && !typeof(HostAccountService).Assembly.GetReferencedAssemblies()
        .Any(
            a => a.Name == "Pcl.Core"
        ),
    "application-compiled-without-core"
);
Check(
    typeof(Pcl.Core.TargetPlatform).Assembly.GetName()
        .Name == "Pcl.Domain",
    "target-platform-is-domain-value"
);
Check(
    typeof(Pcl.Core.TargetPlatform).GetProperty("Current")is null,
    "target-platform-does-not-detect-host"
);
Check(
    JavaRuntimePolicy.ResolveMemory(4096, 8192, true, 1024L * 1024 * 1024) == new MemoryBounds(512, 512),
    "memory-policy-bounded"
);
Check(
    JavaRuntimePolicy.CollectorArguments("generational", 21)
        .Contains("-XX:+ZGenerational") && !JavaRuntimePolicy.CollectorArguments("generational", 25)
        .Contains("-XX:+ZGenerational"),
    "java-version-gc-policy-preserved"
);
Check(
    LegacyArguments.SplitArguments("a \"b c\" ''")
        .SequenceEqual(
            new[]
            {
                "a",
                "b c",
                ""
            }
        ),
    "legacy-argument-pure-parser"
);
Check(
    typeof(Pcl.Core.SkinPixels).Assembly.GetName()
        .Name == "Pcl.Domain",
    "skin-pixels-have-no-account-dependency"
);
var report = args.FirstOrDefault() ?? "result.json";
Directory.CreateDirectory(
    Path.GetDirectoryName(
        Path.GetFullPath(report)
    )!
);
await AccountAppearanceTests.RunAll(
    Path.Combine(
        Path.GetDirectoryName(
            Path.GetFullPath(report)
        )!,
        "appearance"
    ),
    async (name, test) =>
    {
        await test();
        Check(true, name);
    }
);
File.WriteAllText(
    report,
    JsonSerializer.Serialize(
        new
        {
            status = "passed",
            checks = results
        },
        new JsonSerializerOptions
        {
            WriteIndented = true
        }
    )
);
sealed class MemoryStore : IOperationStore
{
    public Dictionary<string, byte[]> Items { get; } = new();

    public IReadOnlyList<byte[]> ReadAll() => Items.Values.ToArray();
    public void Dispose()
    {
    }

    public void Save(string id, byte[] value) => Items[id] = value;
}

sealed class FakeAccountBackend : IAccountBackend
{
    public int Logins;
    public bool Removed;
    public TaskCompletionSource Challenged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AccountIndex Read() => new(
        [
            new("fixture", 0, "Fixture", "uuid", null, false, null, null, null, false)
        ],
        "fixture"
    );
    public async Task<string> LoginAsync(bool remember, Action<AccountChallenge> prompt, CancellationToken ct)
    {
        Logins++;
        prompt(
            new("temporary-code", "https://example.invalid", 60)
        );
        Challenged.SetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return "fixture";
    }

    public void Logout(string id, bool remove) => Removed = remove;
    public Task<AccountLook?> AppearanceAsync(string id, string action, string? value, byte[]? png, bool slim, CancellationToken ct) => Task.FromResult<AccountLook?>(
        new(
            id,
            "Fixture",
            [
                new("skin", "ACTIVE", "https://example.invalid/skin", "slim")
            ],
            []
        )
    );
}

sealed class GuidanceState : ILaunchGuidanceState
{
    public LaunchAccountState Read(string accountId) => new(true, false, true);
}

sealed class FakeGameBackend : IGameLaunchBackend
{
    public int Starts;
    public int Disposals;
    public bool HasRunning => false;
    public bool HasManagedRunning => false;

    public Task WaitForManagedGamesAsync() => Task.CompletedTask;
    public Task<StartedGameProcess> StartAsync(GameLaunchInput input, bool trial, Action beforeSpawn, CancellationToken ct)
    {
        beforeSpawn();
        Starts++;
        return Task.FromResult(
            new StartedGameProcess("game", 123, input.Detached)
        );
    }

    public ManagedGameState Get(string id) => new(id, false, 0, true);
    public GameProcessDetails Details(string id) => new(id, 123, true, "fixture.log");
    public IGameObservation? Borrow(string id) => null;
    public Task StopAsync(string id, CancellationToken ct) => Task.CompletedTask;
    public void Dispose() => Disposals++;
}
