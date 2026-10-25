using System.Diagnostics;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Host;
// 通过已绑定的 OS 进程退出观察验证原 UI 生命周期；不以 TTL、锁文件残留或游戏 PID 夺权。
internal sealed class SessionOwner : IDisposable
{
    private Process? process;
    private Task? exited;
    private ClientIdentity? identity;
    public bool Accept(ClientIdentity value)
    {
        if (value.ClientId.Length != 32 || !Guid.TryParseExact(value.ClientId, "N", out _) || value.ProcessId <= 0)
        {
            throw new OperationRejectedException("CLIENT_IDENTITY_INVALID");
        }

        bool same = identity == value;
        if (identity is not null && !same)
        {
            if (exited?.IsCompleted != true)
            {
                throw new OperationRejectedException("SESSION_IN_USE");
            }
        }

        if (same)
        {
            if (exited?.IsCompleted == true)
            {
                throw new OperationRejectedException("CLIENT_IDENTITY_EXPIRED");
            }

            return true;
        }

        Process candidate;
        try
        {
            candidate = Process.GetProcessById(value.ProcessId);
            if (candidate.HasExited || candidate.StartTime.ToUniversalTime()
                .Ticks != value.StartedUtcTicks)
            {
                candidate.Dispose();
                throw new InvalidOperationException();
            }
        }
        catch (Exception)
        {
            throw new OperationRejectedException("CLIENT_IDENTITY_INVALID");
        }

        process?.Dispose();
        process = candidate;
        identity = value;
        exited = candidate.WaitForExitAsync();
        return false;
    }

    public void Dispose() => process?.Dispose();
}
