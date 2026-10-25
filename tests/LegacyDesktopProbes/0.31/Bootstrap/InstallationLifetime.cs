using Pcl.Core.Application.Installation;

namespace Pcl.Desktop.Bootstrap;
/// <summary>组合根的关闭协调：安装与 UI 收尾都结束后，按顺序只释放一次共享服务及数据目录 lease。</summary>
internal interface IDesktopLifetime
{
    bool Released { get; }

    Task<bool> ShutdownAsync(Task uiQuiescence, TimeSpan budget);
}

internal sealed class InstallationLifetime(
    VersionInstallService installation,
    IReadOnlyList<(string Name, IDisposable Resource)> owned,
    Action<string> observe,
    Func<Task>? stopOther = null
) : IDesktopLifetime
{
    private readonly SemaphoreSlim releaseGate = new(1, 1);
    private bool released;
    public bool Released => Volatile.Read(ref released);

    // 超时只返回未排空，不释放仍在使用的依赖，也不提前篡改任务的持久化状态。
    public async Task<bool> ShutdownAsync(Task uiQuiescence, TimeSpan budget)
    {
        observe("scope.stopping");
        try
        {
            await Task.WhenAll(
                installation.StopAsync(),
                uiQuiescence,
                stopOther?.Invoke() ?? Task.CompletedTask
            )
                .WaitAsync(budget)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            observe("scope.timeout");
            return false;
        }

        await releaseGate.WaitAsync()
            .ConfigureAwait(false);
        try
        {
            if (!released)
            {
                observe("scope.drained");
                List<Exception>? failures = null;
                foreach (var(name, resource)in owned)
                {
                    try
                    {
                        resource.Dispose();
                        observe(name + ".disposed");
                    }
                    catch (Exception ex)
                    {
                        (failures ??= []).Add(ex);
                    }
                }

                Volatile.Write(ref released, true);
                if (failures is not null)
                {
                    throw new AggregateException("服务释放失败。", failures);
                }
            }

            return true;
        }
        finally
        {
            releaseGate.Release();
        }
    }
}
