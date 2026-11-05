using Pcl.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using Pcl.Core.Application.Accounts;
using Pcl.Core.Application.Pages;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Accounts;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Adapters.Operations;

var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
PageCommand Command(string key, string method) => new(key, method, PayloadJson: "{}");
using var lane = new SemaphoreSlim(1, 1);
var executor = new BarrierPages();
var blockedByGame = false;
var pages = new PageOperationService(new MemoryStore(), executor, lane, executionRejection: _ => blockedByGame ? "HOST_WRITER_ACTIVE" : null);
try
{
    var writer = await pages.AcceptAsync(Command("writer", "workspace.settings.save"));
    await executor.WriterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var read = await pages.AcceptAsync(Command("read", "workspace.settings.read"));
    Check((await pages.WaitAsync(read.OperationId).WaitAsync(TimeSpan.FromSeconds(5))).State == "succeeded", "snapshot-read-finishes-while-writer-is-paused");
    Check(!executor.ReleaseWriter.Task.IsCompleted, "read-did-not-release-writer-barrier");
    var queued = await pages.AcceptAsync(Command("queued", "workspace.instances.saveSettings"));
    var cancelled = await pages.AcceptAsync(Command("cancelled", "workspace.settings.import"));
    await pages.CancelAsync(cancelled.OperationId);
    Check(executor.WriteEntries == 1, "second-writer-never-enters-held-lane");
    blockedByGame = true;
    executor.ReleaseWriter.TrySetResult();
    Check((await pages.WaitAsync(writer.OperationId)).State == "succeeded", "accepted-writer-finishes");
    Check((await pages.WaitAsync(queued.OperationId)).ErrorCode == "HOST_WRITER_ACTIVE", "queued-writer-rechecks-game-at-execution");
    Check((await pages.WaitAsync(cancelled.OperationId)).State == "cancelled", "queued-cancellation-observed");
    Check(executor.WriteEntries == 1, "rejected-or-cancelled-writer-never-touches-adapter");
}
finally { executor.ReleaseWriter.TrySetResult(); await pages.StopAsync(); }

var backend = new BarrierAccounts();
var accounts = new HostAccountService(backend, new MemoryStore());
accounts.Restore();
var login = accounts.Begin("login", true);
try
{
    await backend.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(accounts.Snapshot().Accounts.Length == 0 && backend.ReadDuringCommit == 0, "snapshot-does-not-observe-partial-account-commit");
    Check(accounts.Begin("login", true).OperationId == login.OperationId, "login-replay-does-not-create-another-resource-owner");
    try { accounts.Logout("any", true); throw new Exception("logout-not-rejected"); }
    catch (OperationRejectedException e) { Check(e.Code == "ACCOUNT_OPERATION_BUSY", "logout-protects-in-flight-credential-commit"); }
    backend.ReleaseCommit.TrySetResult();
    while (accounts.IsBusy) await Task.Delay(10);
    Check(accounts.Get(login.OperationId).State == "succeeded" && accounts.Snapshot().Accounts.Length == 1, "completed-commit-publishes-new-account-snapshot");
}
finally { backend.ReleaseCommit.TrySetResult(); await accounts.StopAsync(); }

var cancellationBackend = new BarrierAccounts();
var cancellation = new HostAccountService(cancellationBackend, new MemoryStore());
var pending = cancellation.Begin("cancel", false);
await cancellationBackend.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await cancellation.CancelAsync(pending.OperationId);
await cancellation.StopAsync();
Check(cancellation.Get(pending.OperationId).State == "cancelled" && cancellation.Snapshot().Accounts.Length == 0, "cancel-and-shutdown-release-account-owner-without-commit");

Check(PageResourcePolicy.IsConcurrentRead("workspace.settings.read") && PageResourcePolicy.IsConcurrentRead("workspace.catalog.search"), "reviewed-navigation-reads-use-read-lane");
Check(!PageResourcePolicy.IsConcurrentRead("workspace.account.discover") && !PageResourcePolicy.IsConcurrentRead("workspace.resources.batchPrepare"), "mutable-challenges-and-batches-stay-on-writer-lane");
Check(PageResourcePolicy.ConflictsWithAccounts("workspace.account.select") && !PageResourcePolicy.ConflictsWithAccounts("workspace.settings.save"), "account-and-settings-own-different-resources");
Check(PageResourcePolicy.ConflictsWithAccounts("workspace.unknown.read") && !PageResourcePolicy.IsConcurrentRead("workspace.unknown.read"), "unknown-method-never-inherits-read-permission");

var root = Path.GetFullPath(args.Length == 0 ? "artifacts/resource-concurrency" : args[0]);
Directory.CreateDirectory(root);
for (var round = 0; round < 12; round++)
{
    var file = Path.Combine(root, "first-create-" + round + ".json");
    if (File.Exists(file)) throw new Exception("FRESH_TEST_DIRECTORY_REQUIRED");
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var writers = Enumerable.Range(0, 32).Select(async n => { await start.Task; await Task.Run(() => MetadataCacheWriter.Write(file, JsonSerializer.Serialize(new { n }))); }).ToArray();
    start.SetResult(); await Task.WhenAll(writers);
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
    Check(doc.RootElement.GetProperty("n").GetInt32() is >= 0 and < 32, "parallel-first-cache-commit-" + round);
}
Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "cache-commits-leave-no-temporary-files");
Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", checks }));

sealed class MemoryStore : IOperationStore
{
    private readonly ConcurrentDictionary<string, byte[]> rows = new();
    public IReadOnlyList<byte[]> ReadAll() => rows.Values.ToArray();
    public void Save(string operationId, byte[] document) => rows[operationId] = document;
    public void Dispose() { }
}
sealed class BarrierPages : IPageExecutor
{
    public TaskCompletionSource WriterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseWriter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int WriteEntries;
    public async Task<byte[]> ExecuteAsync(string id, PageInput input, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (!PageResourcePolicy.IsConcurrentRead(input.Method))
        {
            Interlocked.Increment(ref WriteEntries); WriterEntered.TrySetResult();
            await ReleaseWriter.Task.WaitAsync(ct);
        }
        return "true"u8.ToArray();
    }
}
sealed class BarrierAccounts : IAccountBackend
{
    public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool committing, committed;
    public int ReadDuringCommit;
    public Pcl.Core.Ports.Accounts.AccountIndex Read()
    {
        if (committing) Interlocked.Increment(ref ReadDuringCommit);
        return new(committed ? [new("test", 1, "test", "test", null, true, null, null, null, true)] : [], committed ? "test" : null);
    }
    public async Task<string> LoginAsync(bool remember, Action<AccountChallenge> prompt, CancellationToken ct)
    {
        committing = true; CommitEntered.TrySetResult();
        try { await ReleaseCommit.Task.WaitAsync(ct); committed = true; return "test"; }
        finally { committing = false; }
    }
    public void Logout(string id, bool remove) => committed = false;
    public Task<AccountLook?> AppearanceAsync(string id, string action, string? value, byte[]? png, bool slim, CancellationToken ct) => Task.FromResult<AccountLook?>(null);
}
