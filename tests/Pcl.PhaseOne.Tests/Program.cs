using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Application.Installation;
using Pcl.Core.Contracts.Installation;
using Pcl.Desktop.Bootstrap;

// 子进程探测真实数据锁，不启动 GUI、不读取账户。
if (args.FirstOrDefault() == "--probe-lock")
{
    try
    {
        var probe = Composition.Create(
            new AppPaths(args[1])
        );
        await probe.Lifetime.ShutdownAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(1)
        );
        Environment.ExitCode = 3;
    }
    catch (IOException)
    {
        Environment.ExitCode = 0;
    }

    return;
}

// 只运行本次应用入口与生命周期的针对性测试；不调用历史功能回归套件。
var output = Path.GetFullPath(
    args.FirstOrDefault() ?? "artifacts/architecture-phase-one/tests"
);
Directory.CreateDirectory(output);
var observations = new List<object>();
var failures = 0;
var target = new InstallationTarget("osx", "aarch64", "test");
VersionInstallRequest Request(string version = "1.21.1") => new(version, "vanilla", target);
InstalledInstance Instance() => new("vanilla-1.21.1", "Minecraft 1.21.1", "1.21.1", "vanilla", "/isolated/game");
void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test()
            .WaitAsync(
                TimeSpan.FromSeconds(15)
            );
        observations.Add(
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
        observations.Add(
            new
            {
                name,
                status = "failed",
                error = ex.ToString()
            }
        );
        Console.Error.WriteLine("FAIL " + name + " " + ex.Message);
    }
}

await Run(
    "request-progress-result",
    async () =>
    {
        var received = new List<VersionInstallProgress>();
        VersionInstallRequest? actual = null;
        var app = new VersionInstallService(
            target,
            (request, progress, ct) =>
            {
                actual = request;
                progress!.Report(
                    new("download", 3, 5, 100, 200)
                );
                return Task.FromResult(
                    Instance()
                );
            }
        );
        var result = await app.InstallAsync(
            Request(),
            new InlineProgress(received.Add)
        );
        Require(
            actual == Request() && result.Instance == Instance() && result.Error is null,
            "Contract 数据未完整转发"
        );
        Require(
            received.Single()
                .Bytes == 100 && received.Single()
                .Total == 5,
            "进度未转发"
        );
        await app.StopAsync();
    }
);
await Run(
    "invalid-input-and-target-rejected-before-backend",
    async () =>
    {
        int calls = 0;
        var app = new VersionInstallService(
            target,
            (r, p, c) =>
            {
                calls++;
                return Task.FromResult(
                    Instance()
                );
            }
        );
        foreach (var request in new[]
        {
            Request("../bad"),
            Request()with
            {
                Loader = "unknown"
            },
            Request()with
            {
                Target = null!
            }
        }

        )
        {
            Require(
                (await app.InstallAsync(request)).Error?.Code == "INVALID_ARGUMENT",
                "无效输入未拒绝"
            );
        }

        Require(
            (await app.InstallAsync(
                Request()with
                {
                    Target = target with
                    {
                        Architecture = "amd64"
                    }
                }
            )).Error?.Code == "UNSUPPORTED_TARGET",
            "目标字段被静默忽略"
        );
        Require(calls == 0, "无效请求进入后端");
    }
);
await Run(
    "error-mapping-does-not-leak-exception",
    async () =>
    {
        var app = new VersionInstallService(
            target,
            (_, _, _) => throw new HttpRequestException("secret-marker", null, HttpStatusCode.ServiceUnavailable)
        );
        var result = await app.InstallAsync(
            Request()
        );
        Require(result.Error?.Code == "NETWORK_ERROR" && result.Instance is null, "网络错误映射错误");
        Require(
            !JsonSerializer.Serialize(result)
                .Contains("secret-marker"),
            "原始异常泄漏"
        );
    }
);
await Run(
    "timeout-is-not-user-cancellation",
    async () =>
    {
        var app = new VersionInstallService(
            target,
            (_, _, _) => throw new TaskCanceledException("timeout-marker")
        );
        var result = await app.InstallAsync(
            Request()
        );
        Require(result.Error?.Code == "TIMEOUT", "未请求取消的超时被误报为取消");
    }
);
await Run(
    "already-cancelled-does-not-install",
    async () =>
    {
        var app = new VersionInstallService(
            target,
            (_, _, _) => throw new Exception("should not run")
        );
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Require(
            (await app.InstallAsync(
                Request(),
                cancellationToken: cancelled.Token
            )).Error?.Code == "CANCELLED",
            "已取消请求仍执行"
        );
    }
);
await Run(
    "concurrent-install-rejected-and-stop-prevents-new-work",
    async () =>
    {
        var finish = new TaskCompletionSource<InstalledInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new VersionInstallService(
            target,
            (_, _, _) => finish.Task
        );
        var first = app.InstallAsync(
            Request()
        );
        Require(
            (await app.InstallAsync(
                Request()
            )).Error?.Code == "INSTALL_BUSY",
            "并发操作未拒绝"
        );
        var stopping = app.StopAsync();
        Require(
            (await app.InstallAsync(
                Request()
            )).Error?.Code == "APPLICATION_STOPPING",
            "退出后仍受理"
        );
        finish.SetResult(
            Instance()
        );
        Require((await first).Instance is not null, "提交成功被迟到取消覆盖");
        await stopping;
    }
);
await Run(
    "shutdown-waits-for-install-cleanup-and-ui-finally",
    async () =>
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<string>();
        var shared = new CountedResource("shared", events);
        var lease = new CountedResource("lease", events);
        var app = new VersionInstallService(
            target,
            async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    await cleanup.Task;
                    throw;
                }

                return Instance();
            },
            events.Enqueue
        );
        var scope = new InstallationLifetime(
            app,
            [
                ("shared", shared),
                ("lease", lease)
            ],
            events.Enqueue
        );
        var install = app.InstallAsync(
            Request()
        );
        var closing = scope.ShutdownAsync(
            ui.Task,
            TimeSpan.FromSeconds(5)
        );
        await cancelled.Task;
        Require(!closing.IsCompleted && shared.Count == 0 && lease.Count == 0, "取消尚未收尾即释放");
        cleanup.SetResult();
        Require((await install).Error?.Code == "CANCELLED", "取消结果丢失");
        Require(!closing.IsCompleted && shared.Count == 0, "UI finally 未结束即释放");
        ui.SetResult();
        Require(await closing, "未排空");
        await Task.WhenAll(
            scope.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            ),
            scope.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            )
        );
        Require(shared.Count == 1 && lease.Count == 1, "重复关闭导致双重释放");
        Require(
            events.ToArray()
                .ToList()
                .IndexOf("shared.dispose") < events.ToArray()
                .ToList()
                .IndexOf("lease.dispose"),
            "数据锁先于共享服务释放"
        );
        File.WriteAllText(
            Path.Combine(output, "shutdown-order.json"),
            JsonSerializer.Serialize(
                events.ToArray(),
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
    }
);
await Run(
    "timeout-keeps-live-resources-and-lease",
    async () =>
    {
        var finish = new TaskCompletionSource<InstalledInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<string>();
        var resource = new CountedResource("live", events);
        var app = new VersionInstallService(
            target,
            (_, _, _) => finish.Task
        );
        var scope = new InstallationLifetime(
            app,
            [
                ("live", resource)
            ],
            events.Enqueue
        );
        var install = app.InstallAsync(
            Request()
        );
        Require(
            !await scope.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromMilliseconds(40)
            ),
            "非协作任务未超时"
        );
        Require(resource.Count == 0 && !scope.Released && !install.IsCompleted, "超时仍释放或伪造终态");
        finish.SetResult(
            Instance()
        );
        await install;
        Require(
            await scope.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            ),
            "最终停止后未释放"
        );
        Require(resource.Count == 1, "释放次数错误");
    }
);
await Run(
    "system-zero-budget-does-not-dispose-active-work",
    async () =>
    {
        var finish = new TaskCompletionSource<InstalledInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new CountedResource(
            "system-live",
            new()
        );
        var app = new VersionInstallService(
            target,
            (_, _, _) => finish.Task
        );
        var scope = new InstallationLifetime(
            app,
            [
                ("resource", resource)
            ],
            _ =>
            {
            }
        );
        var install = app.InstallAsync(
            Request()
        );
        Require(
            !await scope.ShutdownAsync(Task.CompletedTask, TimeSpan.Zero) && resource.Count == 0,
            "系统零预算路径释放活动资源"
        );
        finish.SetResult(
            Instance()
        );
        await install;
        await scope.ShutdownAsync(
            Task.CompletedTask,
            TimeSpan.FromSeconds(1)
        );
    }
);
await Run(
    "diagnostic-failure-does-not-lose-result-or-stop",
    async () =>
    {
        var app = new VersionInstallService(
            target,
            (_, _, _) => Task.FromResult(
                Instance()
            ),
            _ => throw new IOException("diagnostic failed")
        );
        Require(
            (await app.InstallAsync(
                Request()
            )).Instance is not null,
            "诊断异常破坏安装结果"
        );
        await app.StopAsync();
    }
);
await Run(
    "composition-real-data-lock-and-leftover-file",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(output, "scope-data")
        );
        var first = Composition.Create(paths);
        try
        {
            var unexpected = Composition.Create(paths);
            await unexpected.Lifetime.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            );
            throw new InvalidOperationException("第二个组合根取得写入权");
        }
        catch (IOException)
        {
        }

        async Task<int> ProbeProcess()
        {
            var info = new System.Diagnostics.ProcessStartInfo(
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
            info.ArgumentList.Add("--probe-lock");
            info.ArgumentList.Add(paths.Root);
            using var child = System.Diagnostics.Process.Start(info)!;
            await child.WaitForExitAsync();
            return child.ExitCode;
        }

        Require(
            await ProbeProcess() == 0,
            "其他进程取得已占用的数据目录"
        );
        Require(
            await first.Lifetime.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            ),
            "真实根未退出"
        );
        Require(
            File.Exists(
                Path.Combine(paths.Root, ".launcher.lock")
            ),
            "锁文件不应被删除"
        );
        Require(
            await ProbeProcess() == 3,
            "释放后子进程未取得写入权"
        );
        var next = Composition.Create(paths);
        Require(
            await next.Lifetime.ShutdownAsync(
                Task.CompletedTask,
                TimeSpan.FromSeconds(1)
            ),
            "残留文件被误判为占锁"
        );
    }
);
File.WriteAllText(
    Path.Combine(output, "results.json"),
    JsonSerializer.Serialize(
        new
        {
            tests = observations,
            failures
        },
        new JsonSerializerOptions
        {
            WriteIndented = true
        }
    )
);
Environment.ExitCode = failures == 0
    ? 0
    : 1;
sealed class InlineProgress(Action<VersionInstallProgress> report) : IProgress<VersionInstallProgress>
{
    public void Report(VersionInstallProgress value) => report(value);
}

sealed class CountedResource(string name, ConcurrentQueue<string> events) : IDisposable
{
    private int count;
    public int Count => Volatile.Read(ref count);

    public void Dispose()
    {
        Interlocked.Increment(ref count);
        events.Enqueue(name + ".dispose");
    }
}
