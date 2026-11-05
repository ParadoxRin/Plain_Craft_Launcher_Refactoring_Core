using System.Reflection;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;

if (args.FirstOrDefault() == "--data-root")
{
    if (Path.GetFileName(args[1]) == "pages") await PageFixture.RunAsync(args[1]);
    else await FixtureAsync(args[1]);
    return;
}
if (args.Length != 1) throw new ArgumentException("<fresh output directory>");
if (Directory.Exists(args[0]) && Directory.EnumerateFileSystemEntries(args[0]).Any())
    throw new ArgumentException("FRESH_OUTPUT_REQUIRED");
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Throws<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception(name);
}
LoginSnapshot Challenge() => new("operation-fixture", "challenge", new("FIXTURE", "https://example.invalid", 60));
var keys = new List<string>();
int beginnings = 0, reads = 0, shown = 0;
var reconnect = new LoginMonitor((key, _, _) =>
{
    keys.Add(key);
    if (++beginnings == 1) throw new IOException("response lost after admission");
    return Task.FromResult(Challenge());
}, (_, _) =>
{
    if (++reads == 1) throw new OperationRejectedException("HOST_DISCONNECTED");
    return Task.FromResult(new LoginSnapshot("operation-fixture", "succeeded", AccountId: "fixture-account"));
}, (_, _) => Task.CompletedTask, true, TimeSpan.FromMilliseconds(1));
Check(await reconnect.RunAsync(false, _ => shown++, default, default) == "fixture-account", "reconnect-observes-existing-terminal-account");
Check(keys.Count == 2 && keys.Distinct().Count() == 1, "lost-admission-response-reuses-key");
Check(shown == 1, "reconnect-does-not-repeat-prompt");

using var intent = new CancellationTokenSource();
int cancelCalls = 0, terminalReads = 0;
var cancelled = new LoginMonitor((_, _, _) => { intent.Cancel(); return Task.FromResult(Challenge()); },
    (_, _) => Task.FromResult(new LoginSnapshot("operation-fixture", ++terminalReads < 3 ? "challenge" : "cancelled")),
    (_, _) => { cancelCalls++; return Task.CompletedTask; }, false, TimeSpan.FromMilliseconds(1));
await Throws<OperationCanceledException>(() => cancelled.RunAsync(false, _ => throw new Exception("prompt must not appear"), intent.Token, default), "cancel-after-admission-before-prompt");
Check(cancelCalls == 1 && terminalReads == 3, "cancel-waits-for-authoritative-terminal");

int errorCancel = 0, errorReads = 0;
var callbackError = new LoginMonitor((_, _, _) => Task.FromResult(Challenge()),
    (_, _) => Task.FromResult(new LoginSnapshot("operation-fixture", ++errorReads == 2 ? "cancelled" : "challenge")),
    (_, _) => { errorCancel++; return Task.CompletedTask; }, false, TimeSpan.FromMilliseconds(1));
await Throws<InvalidOperationException>(() => callbackError.RunAsync(false, _ => throw new InvalidOperationException("browser unavailable"), default, default), "presentation-failure-preserves-original-error");
Check(errorCancel == 1 && errorReads == 2, "presentation-failure-cancels-host-before-return");

using var early = new CancellationTokenSource(); early.Cancel();
var never = new LoginMonitor((_, _, _) => throw new Exception("must not submit"), (_, _) => throw new Exception(), (_, _) => throw new Exception(), false);
await Throws<OperationCanceledException>(() => never.RunAsync(false, _ => {}, early.Token, default), "pre-cancelled-login-never-submits");
using var abort = new CancellationTokenSource();
var disconnected = new LoginMonitor((_, _, _) => throw new IOException(), (_, _) => throw new Exception(), (_, _) => throw new Exception(), true, TimeSpan.FromMilliseconds(1));
var waiting = disconnected.RunAsync(false, _ => {}, default, abort.Token); abort.Cancel();
await Throws<OperationCanceledException>(() => waiting, "session-abort-stops-reconnect-loop");

var failed = new LoginMonitor((_, _, _) => Task.FromResult(new LoginSnapshot("operation-fixture", "failed", ErrorCode: "LOGIN_FAILED")),
    (_, _) => throw new Exception(), (_, _) => throw new Exception(), false);
await Throws<OperationRejectedException>(() => failed.RunAsync(false, _ => {}, default, default), "failed-terminal-never-returns-account");
Check(typeof(LauncherSession).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("Pcl.")).All(a => a.Name == "Pcl.Contract"), "client-references-contract-only");
Check(!File.Exists(Path.Combine(AppContext.BaseDirectory, "Pcl.Core.dll")), "test-output-has-no-core");

var root = Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
foreach (var dispose in new[] { false, true })
{
    var scenario = Path.Combine(root, dispose ? "dispose" : "shutdown");Directory.CreateDirectory(scenario);
    var client = new HostClient(Environment.ProcessPath!, Assembly.GetExecutingAssembly().Location, scenario);
    var session = new LauncherSession(client);
    var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = session.LoginAsync(false, _ => prompted.TrySetResult(), default);
    var first = await Task.WhenAny(prompted.Task, task, Task.Delay(TimeSpan.FromSeconds(10)));
    if (first == task) await task;
    if (!prompted.Task.IsCompleted) throw new Exception("fixture did not prompt: " + client.SafeDiagnostics);
    await Throws<OperationRejectedException>(() => session.LoginAsync(false, _ => {}, default), "session-rejects-second-login-" + dispose);
    if (dispose) await session.DisposeAsync(); else await session.ShutdownAsync();
    await Throws<OperationCanceledException>(() => task, "session-close-observes-login-terminal-" + dispose);
    var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scenario, "fixture-result.json"))).RootElement;
    Check(state.GetProperty("cancelCalls").GetInt32() == 1 && state.GetProperty("terminalSeen").GetBoolean(), "session-close-cancels-before-transport-dispose-" + dispose);
    if (!dispose) await session.DisposeAsync();
}
foreach (var scenarioName in new[] { "success", "callback" })
{
    var scenario = Path.Combine(root, scenarioName);Directory.CreateDirectory(scenario);
    await using var session = new LauncherSession(new HostClient(Environment.ProcessPath!, Assembly.GetExecutingAssembly().Location, scenario));
    if (scenarioName == "success")
    {
        Check(await session.LoginAsync(false, _ => {}, default) == "fixture-account", "session-success-account-returned");
        Check(session.HasMicrosoftCredential("fixture-account"), "successful-login-refreshes-account-before-return");
    }
    else
        await Throws<InvalidOperationException>(() => session.LoginAsync(false, _ => throw new InvalidOperationException("fixture presentation failure"), default), "session-presentation-failure-observed");
    var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scenario, "fixture-result.json"))).RootElement;
    Check(state.GetProperty("snapshotAfterTerminal").GetBoolean(), "refresh-follows-host-terminal-" + scenarioName);
    await session.ShutdownAsync();
}
{
    var scenario = Path.Combine(root, "pages");Directory.CreateDirectory(scenario);
    await using var session = new LauncherSession(new HostClient(Environment.ProcessPath!, Assembly.GetExecutingAssembly().Location, scenario));
    await session.Client.HelloAsync();
    using var cancelWrite = new CancellationTokenSource();
    var write = session.Workspaces.ExecuteAsync("workspace.instances.backup", "fixture", "{}", ct: cancelWrite.Token);
    while (!File.Exists(Path.Combine(scenario, "pages-fixture.json"))) await Task.Delay(10);
    var read = await session.Workspaces.ExecuteAsync("workspace.settings.read", null, "{}").WaitAsync(TimeSpan.FromSeconds(5));
    Check(read.Value.GetProperty("ok").GetBoolean() && !write.IsCompleted, "read-page-completes-while-write-is-running");
    cancelWrite.Cancel();
    Check(!write.IsCompleted, "write-cancel-does-not-complete-before-host-terminal");
    await Throws<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)), "write-terminal-cancellation-observed");
    var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scenario, "pages-fixture.json"))).RootElement;
    Check(state.GetProperty("beginCount").GetInt32() == 2 && state.GetProperty("uniqueKeys").GetInt32() == 2, "concurrent-pages-have-distinct-admissions");
    Check(state.GetProperty("refreshes").GetInt32() == 2 && state.GetProperty("refreshAfterCancel").GetBoolean(), "each-page-refreshes-after-its-terminal");
    await session.ShutdownAsync();
}
File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));

static async Task FixtureAsync(string root)
{
    int cancelCalls = 0;bool terminalSeen = false, snapshotAfterTerminal = false;
    var success = Path.GetFileName(root) == "success";
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonDocument.Parse(line).RootElement;
        var method = request.GetProperty("method").GetString();
        object result = method switch
        {
            "account.login.begin" => new LoginSnapshot("fixture-operation", "challenge", new("FIXTURE", "https://example.invalid", 60)),
            "account.login.get" => new LoginSnapshot("fixture-operation", cancelCalls > 0 ? "cancelled" : success ? "succeeded" : "challenge", AccountId: success ? "fixture-account" : null),
            "account.login.cancel" => new { cancelled = ++cancelCalls > 0 },
            "desktop.snapshot" => new { accounts = new AccountSnapshot(success ? [new AccountInfo("fixture-account", 1, "Fixture", "fixture", null, false, null, null, null, true)] : [], success ? "fixture-account" : null) },
            "host.shutdown" => new { stopped = true },
            _ => throw new Exception("unexpected fixture method")
        };
        if (method == "account.login.get" && (cancelCalls > 0 || success)) terminalSeen = true;
        if (method == "desktop.snapshot") snapshotAfterTerminal = terminalSeen;
        File.WriteAllText(Path.Combine(root, "fixture-result.json"), JsonSerializer.Serialize(new { cancelCalls, terminalSeen, snapshotAfterTerminal }));
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 1, id = request.GetProperty("id").GetString(), result }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await Console.Out.FlushAsync();
        if (method == "host.shutdown") break;
    }
}
