using System.Collections.Concurrent;
using Pcl.Core.Ports.Operations;

namespace Pcl.Host;
// 监听者只接收不可变快照，回调使用 TryWrite；不把网络等待带入 Application 的存储锁。
internal sealed class OperationEventBroker : IOperationObserver
{
    private readonly ConcurrentDictionary<Guid, Action<OperationChange>> listeners = new();
    public void Changed(OperationChange value)
    {
        foreach (var callback in listeners.Values)
        {
            callback(value);
        }
    }

    public IDisposable Listen(Action<OperationChange> callback)
    {
        var id = Guid.NewGuid();
        listeners[id] = callback;
        return new Registration(
            () => listeners.TryRemove(id, out _)
        );
    }

    private sealed class Registration(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
