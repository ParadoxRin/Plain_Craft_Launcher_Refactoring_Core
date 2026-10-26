using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Adapters.Processes;
using Pcl.Core.Ports.Processes;

// 独立运行测试子进程：父程序退出后，游戏的文件重定向仍必须工作。
if (args.FirstOrDefault() == "--detached-parent")
{
    var directory = args[1];
    using var host = new SystemGameProcessHost();
    var handle = await host.StartAsync(
        new(
            "/bin/sh",
            [
                "-c",
                "sleep 1; printf detached-survived; exit 7"
            ],
            directory
        ),
        new(
            Path.Combine(directory, "detached.log"),
            true
        )
    );
    File.WriteAllText(
        Path.Combine(directory, "detached-id.json"),
        JsonSerializer.Serialize(handle)
    );
    return;
}

if (args.FirstOrDefault() == "--live-refresh")
{
    var original = Path.GetFullPath(args[1]);
    var isolated = new AppPaths(args[2]);
    File.Copy(
        Path.Combine(original, "accounts.json"),
        Path.Combine(isolated.Root, "accounts.json"),
        true
    );
    var secrets = new KeychainAccountSecrets(original);
    var manager = new AccountManager(isolated, secrets);
    var account = manager.Accounts.FirstOrDefault(
        x => x.Kind == AccountKind.Microsoft && x.Remembered
    );
    try
    {
        if (account is null)
        {
            throw new LoginRequiredException("没有可恢复账户");
        }

        using var auth = new MicrosoftAuthentication();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(90)
        );
        var session = await manager.ResolveAsync(account.Id, auth, timeout.Token);
        GameLaunchAuthorization.Require(session);
        var hasCredential = !string.IsNullOrEmpty(
            secrets.Get(account.Id)
        );
        bool gameRendered = false;
        if (args.Contains("--launch-game"))
        {
            using var source = new MinecraftService(isolated);
            var version = source.LoadInstalled();
            var java = await new JavaService(source).EnsureForAsync(version);
            var launcher = new LaunchService(source);
            var plan = await launcher.PrepareAsync(version, java, session, account.ClientId ?? "", isolated.Game);
            var game = await launcher.StartAsync(plan, session);
            try
            {
                var until = DateTime.UtcNow.AddMinutes(3);
                while (!game.HasExited && DateTime.UtcNow < until)
                {
                    var log = File.ReadAllText(game.LogFile);
                    if (log.Contains("Created:") && log.Contains("atlas"))
                    {
                        gameRendered = true;
                        break;
                    }

                    await Task.Delay(250);
                }

                RequireLive(gameRendered && !game.HasExited);
            }
            finally
            {
                if (!game.HasExited)
                {
                    await game.StopAsync();
                }

                await game.Completion;
                await game.DisposeAsync();
            }
        }

        File.WriteAllText(
            args[3],
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    identityMatched = session.Uuid == account.Uuid,
                    ownershipVerified = true,
                    hasCredential,
                    gameRendered
                }
            )
        );
        Console.WriteLine("LIVE_ACCOUNT_REFRESH_PASSED");
    }
    catch (Exception ex)
    {
        File.WriteAllText(
            args[3],
            JsonSerializer.Serialize(
                new
                {
                    status = "failed",
                    errorType = ex.GetType()
                        .Name
                }
            )
        );
        Console.WriteLine(
            "LIVE_ACCOUNT_REFRESH_FAILED " + ex.GetType()
                .Name
        );
        Environment.ExitCode = 1;
    }

    return;
}

if (args.FirstOrDefault() == "--verify-live-detached")
{
    var data = Path.GetFullPath(args[1]);
    var report = args[2];
    Process? child = null;
    try
    {
        var receipt = JsonFiles.Read(
            Path.Combine(data, "evidence/detached-launch.json")
        );
        var pid = (int)receipt["pid"]!;
        var file = (string)receipt["log"]!;
        child = Process.GetProcessById(pid);
        var identity = child.StartTime;
        var until = DateTime.UtcNow.AddMinutes(3);
        var rendered = false;
        while (!child.HasExited && DateTime.UtcNow < until)
        {
            var text = File.Exists(file)
                ? File.ReadAllText(file)
                : "";
            if (text.Contains("Created:") && text.Contains("atlas"))
            {
                rendered = true;
                break;
            }

            await Task.Delay(250);
        }

        RequireLive(rendered && !child.HasExited && child.StartTime == identity);
        File.WriteAllText(
            report,
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    parentExited = true,
                    rendered,
                    childAliveAfterParent = true,
                    log = file
                }
            )
        );
        Console.WriteLine("LIVE_DETACHED_PASSED");
    }
    finally
    {
        if (child is not null)
        {
            if (!child.HasExited)
            {
                child.Kill(true);
                await child.WaitForExitAsync();
            }

            child.Dispose();
        }
    }

    return;
}

static void RequireLive(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("独立游戏未在父进程退出后完成初始化");
    }
}

var output = Path.GetFullPath(
    args.FirstOrDefault() ?? "artifacts/architecture-phase-two/tests"
);
Directory.CreateDirectory(output);
var results = new List<object>();
int failures = 0;
void Require(bool condition, string reason)
{
    if (!condition)
    {
        throw new InvalidOperationException(reason);
    }
}

async Task Run(string name, Func<Task> body)
{
    // 无凭据架构验收不触碰系统钥匙串；明确记为未执行，不能计入通过数。
    if (args.Contains("--without-keychain") && name == "real-keychain-compatible-scope-update-and-delete")
    {
        results.Add(
            new
            {
                name,
                status = "not-run",
                reason = "explicit-without-keychain"
            }
        );
        return;
    }

    try
    {
        await body()
            .WaitAsync(
                TimeSpan.FromSeconds(40)
            );
        results.Add(
            new
            {
                name,
                status = "passed"
            }
        );
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures++;
        results.Add(
            new
            {
                name,
                status = "failed",
                error = ex.GetType()
                    .Name + ": " + ex.Message
            }
        );
        Console.Error.WriteLine(
            "FAIL " + name + " " + ex.GetType()
                .Name
        );
    }

    File.WriteAllText(
        Path.Combine(output, "results.json"),
        JsonSerializer.Serialize(
            new
            {
                failures,
                results
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}

ProcessSpec Shell(string script) => new(
    "/bin/sh",
    [
        "-c",
        script
    ],
    output
);
await Run(
    "short-command-dual-stream-exit-and-argv",
    async () =>
    {
        var runner = new SystemShellCommandRunner();
        var literal = "space ; dollar $(not-executed) ' quote";
        var result = await runner.RunAsync(
            new(
                "/bin/sh",
                [
                    "-c",
                    "printf '%s' \"$1\"; printf err >&2; exit 23",
                    "fixture",
                    literal
                ],
                output
            )
        );
        Require(
            result.ExitCode == 23 && result.StandardOutput.TrimEnd() == literal && result.StandardError.TrimEnd() == "err",
            "参数或退出状态不一致"
        );
    }
);
await Run(
    "short-command-large-dual-stream-and-redaction",
    async () =>
    {
        var runner = new SystemShellCommandRunner();
        const string secret = "phase-two-sensitive-value";
        var spec = new ProcessSpec(
            "/bin/sh",
            [
                "-c",
                "i=0; while [ $i -lt 12000 ]; do printf 'line\\n'; printf 'err\\n' >&2; i=$((i+1)); done; printf '%s' \"$1\"",
                "fixture",
                secret
            ],
            output
        )
        {
            SensitiveValues = [
                secret
            ]
        };
        var result = await runner.RunAsync(spec);
        Require(
            result.ExitCode == 0 && result.StandardOutput.Contains("line") && result.StandardError.Contains("err"),
            "双流排空失败"
        );
        Require(
            !result.StandardOutput.Contains(secret) && result.StandardOutput.Contains("<redacted>"),
            "敏感输出未脱敏"
        );
        Require(
            !spec.ToString()!.Contains(secret),
            "内部输入 ToString 泄漏参数"
        );
    }
);
await Run(
    "short-command-cancel-cleans-owned-process-tree",
    async () =>
    {
        using var cancellation = new CancellationTokenSource();
        int child = 0;
        var outputLine = new InlineLines(
            line =>
            {
                if (int.TryParse(line.Text, out var id))
                {
                    child = id;
                    cancellation.Cancel();
                }
            }
        );
        try
        {
            await new SystemShellCommandRunner().RunAsync(
                Shell("sleep 60 & child=$!; printf '%s\\n' \"$child\"; wait"),
                outputLine,
                cancellation.Token
            );
            throw new Exception("未取消");
        }
        catch (OperationCanceledException)
        {
        }

        Require(child > 0, "未观察到后代进程");
        bool alive = false;
        try
        {
            using var process = Process.GetProcessById(child);
            alive = !process.HasExited;
        }
        catch (ArgumentException)
        {
        }

        Require(!alive, "取消后后代仍存活");
    }
);
await Run(
    "java-inspection-through-short-port-real-java21",
    async () =>
    {
        var java = Path.Combine(
            Directory.GetCurrentDirectory(),
            "runtime/apple-silicon-1.21.1/runtime/java-runtime-delta-macos-arm64/jre.bundle/Contents/Home/bin/java"
        );
        var runtime = await JavaService.InspectAsync(
            java,
            commands: new SystemShellCommandRunner()
        );
        Require(runtime.MajorVersion == 21 && runtime.Architecture == "aarch64", "真实 Java 识别不匹配");
    }
);
await Run(
    "forge-installer-uses-injected-short-port",
    async () =>
    {
        var runner = new RecordingCommandRunner();
        var exit = await ForgeService.RunInstallerAsync(
            "java",
            "installer.jar",
            output,
            "neoforge",
            Path.Combine(output, "installer.log"),
            null,
            default,
            runner
        );
        Require(
            exit == 0 && runner.Last!.Arguments.Contains("--install-client") && File.ReadAllText(
                Path.Combine(output, "installer.log")
            )
                .Contains("Task:"),
            "安装器未使用端口或日志丢失"
        );
    }
);
// 原有启动前短命令和节点查询走同一 Port；替身验证调用接线，真实 shell 验证错误路径。
await Run(
    "launch-hook-wait-success-failure-and-timeout",
    async () =>
    {
        using var hooks = new LaunchHookRunner(
            new SystemShellCommandRunner()
        );
        await hooks.RunAsync(
            new(true, "/bin/sh", "-c \"exit 0\""),
            output
        );
        bool failed = false;
        try
        {
            await hooks.RunAsync(
                new(true, "/bin/sh", "-c \"exit 9\""),
                output
            );
        }
        catch (IOException)
        {
            failed = true;
        }

        bool timedOut = false;
        try
        {
            await hooks.RunAsync(
                new(true, "/bin/sh", "-c \"sleep 30\"", true, 1),
                output
            );
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        Require(failed && timedOut, "启动前命令失败或超时仍放行");
    }
);
await Run(
    "peer-query-uses-injected-short-port",
    async () =>
    {
        var commands = new RecordingCommandRunner();
        using var unusedProcess = new Process();
        var link = new LinkProcess(unusedProcess, 12345, "/fixture/easytier-cli", commands);
        await link.PeersAsync();
        Require(
            commands.Last!.Arguments.SequenceEqual(
                new[]
                {
                    "-o",
                    "json",
                    "-p",
                    "127.0.0.1:12345",
                    "peer"
                }
            ),
            "节点查询参数或接线改变"
        );
    }
);
await Run(
    "oversized-output-is-bounded-and-marked",
    async () =>
    {
        var result = await new SystemShellCommandRunner().RunAsync(
            Shell("dd if=/dev/zero bs=70000 count=1 2>/dev/null | tr '\\000' x")
        );
        Require(
            result.ExitCode == 0 && result.Truncated && result.StandardOutput.Contains("oversized output line omitted") && result.StandardOutput.Length < 100,
            "无换行输出未受控截断"
        );
    }
);
await Run(
    "long-start-wait-log-drain-and-release",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        var log = Path.Combine(output, "natural.log");
        var handle = await host.StartAsync(
            Shell("printf first; printf err >&2; sleep 0.2; printf last; exit 17"),
            new(log)
        );
        var lines = new List<ProcessLogLine>();
        await foreach (var line in host.ReadAsync(handle))
        {
            lines.Add(line);
        }

        Require(
            await host.WaitForExitAsync(handle) == 17 && !host.IsRunning(handle),
            "自然退出码不正确"
        );
        Require(
            File.ReadAllText(log)
                .Contains("last") && lines.Any(
                x => x.Stream == "stderr"
            ),
            "退出前日志未排空"
        );
        await host.ReleaseAsync(handle);
        await host.ReleaseAsync(handle);
    }
);
await Run(
    "cancel-wait-does-not-kill-game-and-stop-is-explicit",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        var handle = await host.StartAsync(
            Shell("sleep 60"),
            new(
                Path.Combine(output, "wait.log")
            )
        );
        using var wait = new CancellationTokenSource(80);
        try
        {
            await host.WaitForExitAsync(handle, wait.Token);
            throw new Exception("等待未取消");
        }
        catch (OperationCanceledException)
        {
        }

        Require(
            host.IsRunning(handle),
            "取消等待杀死游戏"
        );
        await host.TerminateAsync(handle);
        Require(
            !host.IsRunning(handle),
            "显式停止未结束游戏"
        );
        await host.ReleaseAsync(handle);
    }
);
await Run(
    "soft-terminate-observes-natural-handler-exit",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        var file = Path.Combine(output, "soft.log");
        var handle = await host.StartAsync(
            Shell("trap 'printf handled; exit 0' TERM; printf ready\\n; while :; do sleep 0.05; done"),
            new(file)
        );
        await Task.Delay(150);
        await host.TerminateAsync(handle, force: false);
        Require(
            await host.WaitForExitAsync(handle) == 0 && File.ReadAllText(file)
                .Contains("handled"),
            "温和退出信号未保留自然退出码"
        );
        await host.ReleaseAsync(handle);
    }
);
await Run(
    "forged-handle-cannot-control-owned-process",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        var handle = await host.StartAsync(
            Shell("sleep 60"),
            new(
                Path.Combine(output, "identity.log")
            )
        );
        bool denied = false;
        try
        {
            await host.TerminateAsync(
                handle with
                {
                    Id = Guid.NewGuid()
                        .ToString("N")
                }
            );
        }
        catch (InvalidOperationException)
        {
            denied = true;
        }

        Require(
            denied && host.IsRunning(handle),
            "伪造句柄被接受"
        );
        await host.TerminateAsync(handle);
        await host.ReleaseAsync(handle);
    }
);
await Run(
    "host-dispose-does-not-kill-active-game",
    async () =>
    {
        var host = new SystemGameProcessHost();
        var handle = await host.StartAsync(
            Shell("sleep 0.4; printf alive-after-dispose"),
            new(
                Path.Combine(output, "dispose.log")
            )
        );
        var exit = host.WaitForExitAsync(handle);
        host.Dispose();
        Require(
            await exit == 0 && File.ReadAllText(
                Path.Combine(output, "dispose.log")
            )
                .Contains("alive-after-dispose"),
            "Dispose 杀死游戏或丢失日志"
        );
    }
);
await Run(
    "detached-survives-parent-and-host-disposal",
    async () =>
    {
        var info = new ProcessStartInfo(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                ".tooling/dotnet/dotnet"
            )
        )
        {
            UseShellExecute = false
        };
        info.ArgumentList.Add(
            System.Reflection.Assembly.GetExecutingAssembly()
                .Location
        );
        info.ArgumentList.Add("--detached-parent");
        info.ArgumentList.Add(output);
        using var parent = Process.Start(info)!;
        await parent.WaitForExitAsync();
        Require(parent.ExitCode == 0, "测试父进程退出失败");
        var until = DateTime.UtcNow.AddSeconds(8);
        var log = Path.Combine(output, "detached.log");
        while ((!File.Exists(log) || !File.ReadAllText(log)
            .Contains("detached-survived")) && DateTime.UtcNow < until)
        {
            await Task.Delay(100);
        }

        Require(
            File.ReadAllText(log)
                .Contains("detached-survived"),
            "父进程退出后独立游戏未继续"
        );
    }
);
await Run(
    "launch-service-preserves-authorization-and-uses-host",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        using var source = new MinecraftService(
            new AppPaths(
                Path.Combine(output, "launch-scope")
            ),
            gameProcesses: host
        );
        var launcher = new LaunchService(source);
        var plan = new LaunchPlan(
            "/bin/sh",
            [
                "-c",
                "printf launch-port"
            ],
            output
        );
        bool denied = false;
        try
        {
            await launcher.StartAsync(
                plan,
                new(
                    "not-valid",
                    new string ('0', 32),
                    "invalid",
                    "legacy",
                    false
                )
            );
        }
        catch (LoginRequiredException)
        {
            denied = true;
        }

        Require(denied, "授权检查被绕过");
        var game = await launcher.StartAsync(
            plan,
            AccountManager.OfflineSession("StageTwo")
        );
        Require(
            await game.Completion == 0 && File.ReadAllText(game.LogFile)
                .Contains("launch-port"),
            "真实核心未使用进程端口"
        );
        await game.DisposeAsync();
    }
);
await Run(
    "credential-port-remember-reload-and-logout",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(output, "fake-account")
        );
        var secrets = new MemorySecrets();
        var manager = new AccountManager(paths, secrets);
        const string uuid = "12345678901234567890123456789012";
        var account = manager.SaveMicrosoft(
            new(
                new("Fixture", uuid, "fixture-access", "msa", false),
                "fixture-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                "11111111-2222-3333-4444-555555555555"
            ),
            true
        );
        Require(secrets.Items[account.Id] == "fixture-refresh", "凭据未走接口存储");
        var reloaded = new AccountManager(paths, secrets);
        Require(
            reloaded.Accounts.Single()
                .Id == account.Id && reloaded.Accounts.Single()
                .Remembered,
            "账户身份或记住标记改变"
        );
        reloaded.Logout(account.Id);
        Require(
            !secrets.Items.ContainsKey(account.Id) && !reloaded.Accounts.Single()
                .Remembered,
            "退出后凭据未删除"
        );
        using var auth = new MicrosoftAuthentication();
        bool denied = false;
        try
        {
            await reloaded.ResolveAsync(account.Id, auth);
        }
        catch (LoginRequiredException)
        {
            denied = true;
        }

        Require(denied, "已退出账户仍可恢复");
        var offline = reloaded.AddOffline("OfflineTwo");
        Require(
            GameLaunchAuthorization.IsOfflineIdentity(
                await reloaded.ResolveAsync(offline.Id, auth)
            ),
            "离线登录改变"
        );
    }
);
await Run(
    "real-keychain-compatible-scope-update-and-delete",
    async () =>
    {
        var scope = Path.Combine(output, "keychain-synthetic");
        IAccountSecrets adapter = new KeychainAccountSecrets(scope);
        IAccountSecrets legacy = new MacAccountSecrets(scope);
        var id = "phase-two-test-" + Guid.NewGuid()
            .ToString("N");
        var secret = Guid.NewGuid()
            .ToString("N");
        try
        {
            adapter.Set(id, secret);
            Require(
                legacy.Get(id) == secret,
                "旧命名空间无法读取新实现"
            );
            legacy.Set(id, secret + "updated");
            Require(
                adapter.Get(id) == secret + "updated",
                "更新后无法恢复"
            );
            legacy.Delete(id);
            Require(
                adapter.Get(id)is null,
                "删除后仍有凭据"
            );
        }
        finally
        {
            adapter.Delete(id);
        }

        await Task.CompletedTask;
    }
);
// 版本服务借用共享 Host；只有组合根关闭时释放一次。
await Run(
    "borrowed-host-and-root-single-release",
    async () =>
    {
        using var host = new SystemGameProcessHost();
        using var source = new MinecraftService(
            new AppPaths(
                Path.Combine(output, "borrowed")
            ),
            gameProcesses: host
        );
        source.ForVersion("1.20.1")
            .Dispose();
        var handle = await host.StartAsync(
            Shell("exit 0"),
            new(
                Path.Combine(output, "borrowed.log")
            )
        );
        Require(
            await host.WaitForExitAsync(handle) == 0,
            "版本服务提前释放了借用的游戏 Host"
        );
        await host.ReleaseAsync(handle);
        var rootPaths = new AppPaths(
            Path.Combine(output, "root-single")
        );
        var scope = new Pcl.Host.HostRuntime(
            rootPaths,
            new Pcl.Host.OperationEventBroker()
        );
        var shutdown = scope.ShutdownAsync(true);
        Require(
            ReferenceEquals(
                shutdown,
                scope.ShutdownAsync(true)
            ),
            "退出任务没有复用"
        );
        await shutdown;
        var disposal = scope.DisposeAsync()
            .AsTask();
        Require(
            ReferenceEquals(
                disposal,
                scope.DisposeAsync()
                    .AsTask()
            ),
            "组合根重复释放没有复用"
        );
        await disposal;
        // 同一目录再次构造真实根，证明私有 receipt 文件锁已经释放。
        await using (var reopened = new Pcl.Host.HostRuntime(
            rootPaths,
            new Pcl.Host.OperationEventBroker()
        ))
        {
            await reopened.ShutdownAsync(true);
        }

        bool stopped = false;
        try
        {
            await scope.Source.GameProcesses.StartAsync(
                Shell("exit 0"),
                new(
                    Path.Combine(output, "should-not-start.log")
                )
            );
        }
        catch (ObjectDisposedException)
        {
            stopped = true;
        }

        Require(stopped, "已释放组合根仍可新建游戏");
    }
);
Environment.ExitCode = failures == 0
    ? 0
    : 1;
sealed class InlineLines(Action<ProcessLogLine> action) : IProgress<ProcessLogLine>
{
    public void Report(ProcessLogLine value) => action(value);
}

sealed class RecordingCommandRunner : IShellCommandRunner
{
    public ProcessSpec? Last { get; private set; }

    public Task<CommandResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessLogLine>? output = null,
        CancellationToken ct = default
    )
    {
        Last = spec;
        output?.Report(
            new(1, "stdout", "Task: fixture")
        );
        return Task.FromResult(
            new CommandResult(0, "", "", false)
        );
    }
}

sealed class MemorySecrets : IAccountSecrets
{
    public Dictionary<string, string> Items { get; } = new();

    public string? Get(string key) => Items.GetValueOrDefault(key);
    public void Set(string key, string value) => Items[key] = value;
    public void Delete(string key) => Items.Remove(key);
}
