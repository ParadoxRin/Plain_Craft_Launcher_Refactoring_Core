namespace Pcl.Core.Ports.Operations;
// Application 输出受控快照字节；Ports 不引用 Contract。实现必须非阻塞，失败不得改写任务事实。
public sealed record OperationChange(string OperationId, long Sequence, string State, string Stage, byte[] Snapshot);
public interface IOperationObserver
{
    void Changed(OperationChange value);
}
