using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

namespace Pcl.Host;
// 进度按 50ms 合并；状态/阶段变化有界可靠排队，满时断连接并要求快照同步，不阻塞安装提交。
internal sealed class OperationSubscriptions : IAsyncDisposable
{
    private sealed class Watch(EventSubscription lease, long last)
    {
        public EventSubscription Lease = lease;
        public long Last = last;
        public string LastCode = "";
        public bool InitialSynced;
    }

    private readonly ConcurrentDictionary<string, Watch> watches = new();
    private readonly ConcurrentDictionary<string, HostEvent> progress = new();
    private readonly Channel<HostEvent> reliable = Channel.CreateBounded<HostEvent>(128);
    private readonly CancellationTokenSource stop = new();
    private readonly string hostId;
    private readonly Func<string, string, Task<JsonElement>> snapshot;
    private readonly Func<HostEvent, Task> send;
    private readonly Action disconnect;
    private readonly IDisposable registration;
    private readonly Task execution;
    public OperationSubscriptions(
        string hostId,
        OperationEventBroker broker,
        Func<string, string, Task<JsonElement>> snapshot,
        Func<HostEvent, Task> send,
        Action disconnect
    )
    {
        this.hostId = hostId;
        this.snapshot = snapshot;
        this.send = send;
        this.disconnect = disconnect;
        registration = broker.Listen(Changed);
        execution = RunAsync();
    }

    public EventSubscription Subscribe(string id, string kind, string subject, long after)
    {
        if (!id.StartsWith("sub-", StringComparison.Ordinal) || !Guid.TryParseExact(id[4..], "N", out _) || kind is not ("operation" or "game" or "page") || subject.Length is < 1 or > 128 || after < -1 || watches.Count >= 64)
        {
            throw new OperationRejectedException("SUBSCRIPTION_ARGUMENT_OR_LIMIT");
        }

        var lease = new EventSubscription(id, hostId, kind, subject);
        if (!watches.TryAdd(
            id,
            new(lease, after)
        ))
        {
            var existing = watches[id].Lease;
            if (existing.Kind != kind || existing.SubjectId != subject)
            {
                throw new OperationRejectedException("SUBSCRIPTION_REUSED");
            }

            return existing;
        }

        return lease;
    }

    public void Unsubscribe(string id)
    {
        watches.TryRemove(id, out _);
        progress.TryRemove(id, out _);
    }

    private void Changed(OperationChange change)
    {
        foreach (var pair in watches)
        {
            var watch = pair.Value;
            if (watch.Lease.SubjectId != change.OperationId || watch.Lease.Kind == "game")
            {
                continue;
            }

            using var doc = JsonDocument.Parse(change.Snapshot);
            var value = new HostEvent(
                pair.Key,
                hostId,
                watch.Lease.Kind,
                change.OperationId,
                change.Sequence,
                doc.RootElement.Clone()
            );
            var code = change.State + ":" + change.Stage;
            var previous = Interlocked.Exchange(ref watch.LastCode, code);
            if (previous != code || change.State is "succeeded" or "failed" or "cancelled" or "interrupted")
            {
                if (!reliable.Writer.TryWrite(value))
                {
                    disconnect();
                }
            }
            else
            {
                progress[pair.Key] = value;
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var batch = new List<HostEvent>();
                while (reliable.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                foreach (var key in progress.Keys)
                {
                    if (progress.TryRemove(key, out var item))
                    {
                        batch.Add(item);
                    }
                }

                foreach (var value in batch.OrderBy(
                    x => x.Sequence
                ))
                {
                    await Deliver(value);
                }

                foreach (var pair in watches)
                {
                    // 快照覆盖订阅前发生的变化；游戏退出也有独立水位，不依赖 UI 的 Java Process。
                    if (pair.Value.Lease.Kind != "game" && pair.Value.InitialSynced)
                    {
                        continue;
                    }

                    JsonElement row;
                    try
                    {
                        row = await snapshot(pair.Value.Lease.Kind, pair.Value.Lease.SubjectId);
                    }
                    catch (OperationRejectedException)
                    {
                        continue;
                    }

                    var seq = row.TryGetProperty("sequence", out var number)
                        ? number.GetInt64()
                        : row.GetProperty("state")
                            .GetString() == "running"
                            ? 1L
                            : 2L;
                    pair.Value.InitialSynced = true;
                    if (seq > Volatile.Read(ref pair.Value.Last))
                    {
                        await Deliver(
                            new(
                                pair.Key,
                                hostId,
                                pair.Value.Lease.Kind,
                                pair.Value.Lease.SubjectId,
                                seq,
                                row
                            )
                        );
                    }
                }

                await Task.Delay(50, stop.Token);
            }
        }
        catch (OperationCanceledException)when (stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            disconnect();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task Deliver(HostEvent value)
    {
        if (watches.TryGetValue(value.SubscriptionId, out var watch) && value.Sequence > Volatile.Read(ref watch.Last))
        {
            await send(value);
            Volatile.Write(ref watch.Last, value.Sequence);
        }
    }

    public async ValueTask DisposeAsync()
    {
        registration.Dispose();
        await stop.CancelAsync();
        await execution;
        stop.Dispose();
        watches.Clear();
    }
}
