using Pcl.Core.Contracts.Installation;

namespace Pcl.Core.Application.Installation;
/// <summary>安装等待门面：组合根经 v3 runner 执行；测试接缝验证关闭责任，不引用旧 Core 安装实现。</summary>
public sealed class VersionInstallService : IVersionInstallService
{
    private readonly object gate = new();
    private readonly InstallationTarget target;
    private readonly Func<VersionInstallRequest, IProgress<VersionInstallProgress>?, CancellationToken, Task<InstalledInstance>> install;
    private readonly Action<string> observe;
    private Work? active;
    private Task? stopping;
    private readonly Pcl.Core.Application.Operations.InstallOperationService? operationsV3;
    public Pcl.Core.Contracts.Operations.IVersionInstallOperations? Operations => operationsV3;

    // 新组合根使用 v3 runner；保留旧测试/调用构造，不隐式为每个窗口启动另一套 runner。
    public VersionInstallService(
        Pcl.Core.Application.Operations.InstallOperationService operations,
        InstallationTarget target,
        Action<string>? observe = null
    ) : this(
        target,
        (request, progress, ct) => throw new NotSupportedException(),
        observe
    )
    {
        operationsV3 = operations;
    }

    // 内部依赖接缝用于生命周期测试，生产组合根始终使用上面的真实安装门面。
    internal VersionInstallService(
        InstallationTarget target,
        Func<VersionInstallRequest, IProgress<VersionInstallProgress>?, CancellationToken, Task<InstalledInstance>> install,
        Action<string>? observe = null
    )
    {
        this.target = target;
        this.install = install;
        this.observe = name =>
        {
            try
            {
                observe?.Invoke(name);
            }
            catch (Exception)
            {
                // 诊断订阅失败不能让安装调用丢终态或阻止退出。
            }
        };
    }

    // 在同一作用域内拒绝并发安装；先登记活动调用，再执行真实流程，以便退出路径始终能等待它。
    public Task<VersionInstallResult> InstallAsync(
        VersionInstallRequest request,
        IProgress<VersionInstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (operationsV3 is not null)
        {
            return Pcl.Core.Application.Operations.OperationInstallBridge.InstallAsync(operationsV3, request, progress, cancellationToken);
        }

        lock (gate)
        {
            if (stopping is not null)
            {
                return Failure("APPLICATION_STOPPING");
            }

            if (request is null || request.Target is null || string.IsNullOrWhiteSpace(request.GameVersion))
            {
                return Failure("INVALID_ARGUMENT");
            }

            try
            {
                Pcl.Core.Domain.GameVersionIdentity.Validate(request.GameVersion);
            }
            catch (ArgumentException)
            {
                return Failure("INVALID_ARGUMENT");
            }
            catch (InvalidDataException)
            {
                return Failure("INVALID_ARGUMENT");
            }

            if (request.Loader is not ("auto" or "vanilla" or "fabric" or "quilt" or "forge" or "neoforge" or "optifine" or "liteloader"))
            {
                return Failure("INVALID_ARGUMENT");
            }

            if (request.Target != target)
            {
                return Failure("UNSUPPORTED_TARGET");
            }

            if (active is not null)
            {
                return Failure("INSTALL_BUSY");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Failure("CANCELLED");
            }

            var work = new Work(
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            );
            active = work;
            work.Execution = ExecuteAsync(work, request, progress);
            return work.Completion.Task;
        }
    }

    // 停止受理并取消活动调用；此 Task 被组合根持有，等待预算由组合根统一施加。
    public Task StopAsync()
    {
        if (operationsV3 is not null)
        {
            return operationsV3.StopAsync();
        }

        lock (gate)
        {
            if (stopping is not null)
            {
                return stopping;
            }

            observe("install.stopping");
            stopping = active is null
                ? Task.CompletedTask
                : Task.WhenAll(
                    active.Cancellation.CancelAsync(),
                    active.Completion.Task
                );
            return stopping;
        }
    }

    // 异常只映射成稳定代码；原安装历史、依赖校验和提交仍由原流程负责。
    private async Task ExecuteAsync(Work work, VersionInstallRequest request, IProgress<VersionInstallProgress>? progress)
    {
        VersionInstallResult result;
        try
        {
            observe("install.entered");
            var instance = await install(request, progress, work.Cancellation.Token)
                .ConfigureAwait(false);
            result = new(instance, null);
            observe("install.succeeded");
        }
        catch (OperationCanceledException)when (work.Cancellation.IsCancellationRequested)
        {
            result = new(
                null,
                new("CANCELLED")
            );
            observe("install.cancelled");
        }
        catch (Exception ex)
        {
            result = new(
                null,
                new(
                    ex switch
                    {
                        PlatformNotSupportedException => "UNSUPPORTED_TARGET",
                        NotSupportedException => "UNSUPPORTED",
                        UnauthorizedAccessException => "ACCESS_DENIED",
                        HttpRequestException => "NETWORK_ERROR",
                        OperationCanceledException => "TIMEOUT",
                        InvalidDataException => "INVALID_DATA",
                        IOException => "IO_ERROR",
                        ArgumentException => "INVALID_ARGUMENT",
                        _ => "INSTALL_FAILED"
                    }
                )
            );
            observe("install.failed");
        }

        lock (gate)
        {
            active = null;
            work.Cancellation.Dispose();
            work.Completion.TrySetResult(result);
        }
    }

    private static Task<VersionInstallResult> Failure(string code) => Task.FromResult(
        new VersionInstallResult(
            null,
            new(code)
        )
    );
    private sealed class Work(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<VersionInstallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Execution { get; set; }
    }
}
