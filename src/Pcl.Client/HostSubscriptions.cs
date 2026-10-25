using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;
public sealed partial class HostClient
{
    public string HostId { get; private set; } = "";
    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();

    // 注册本地缓冲在前，服务端订阅与权威快照在后，覆盖“查询后再订阅”的丢事件窗口。
    public async Task<HostSubscription> SubscribeAsync(string kind, string subjectId, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var subscription = new HostSubscription(
            this,
            "sub-" + Guid.NewGuid()
                .ToString("N"),
            kind,
            subjectId
        );
        if (!subscriptions.TryAdd(subscription.Id, subscription))
        {
            throw new OperationRejectedException("SUBSCRIPTION_REUSED");
        }

        try
        {
            while (true)
            {
                try
                {
                    await RestoreSubscriptionAsync(subscription, ct);
                    break;
                }
                catch (Exception ex)when (reconnectable && IsTransientConnectionFailure(ex, ct))
                {
                    // 订阅建立与快照之间断线时保留原订阅身份，直至重新同步或调用者停止等待。
                    await RecoverSubscriptionConnectionAsync(ct);
                }
            }

            return subscription;
        }
        catch
        {
            subscriptions.TryRemove(subscription.Id, out _);
            subscription.Close();
            throw;
        }
    }

    private static bool IsTransientConnectionFailure(Exception ex, CancellationToken ct) => ex is IOException or ObjectDisposedException || ex is OperationRejectedException rejected && rejected.Code is "HOST_DISCONNECTED" or "HOST_CONNECT_TIMEOUT" || ex is OperationCanceledException && !ct.IsCancellationRequested;
    internal async Task RecoverSubscriptionConnectionAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await ReconnectAsync(ct);
                return;
            }
            catch (Exception ex)when (IsTransientConnectionFailure(ex, ct))
            {
                await Task.Delay(250, ct);
            }
        }
    }

    private async Task RestoreSubscriptionAsync(HostSubscription subscription, CancellationToken ct)
    {
        if (subscription.IsClosed)
        {
            return;
        }

        await SendCore<EventSubscription>(
            "event.subscribe",
            new
            {
                subscriptionId = subscription.Id,
                kind = subscription.Kind,
                subjectId = subscription.SubjectId,
                afterSequence = subscription.LastSequence
            },
            ct
        );
        var snapshot = await SendCore<JsonElement>(
            subscription.Kind switch
            {
                "game" => "game.get",
                "page" => "page.get",
                _ => "operation.get"
            },
            subscription.Kind == "game"
                ? (object)new
                {
                    gameProcessId = subscription.SubjectId
                }
                : new
                {
                    operationId = subscription.SubjectId
                },
            ct
        );
        var sequence = snapshot.TryGetProperty("sequence", out var seq)
            ? seq.GetInt64()
            : snapshot.GetProperty("state")
                .GetString() == "running"
                ? 1L
                : 2L;
        if (!subscription.Enqueue(
            new(
                subscription.Id,
                HostId,
                subscription.Kind,
                subscription.SubjectId,
                sequence,
                snapshot
            )
        ))
        {
            input?.Dispose();
            throw new OperationRejectedException("SUBSCRIPTION_RESYNC_REQUIRED");
        }
    }

    internal async Task RemoveSubscriptionAsync(HostSubscription subscription)
    {
        subscriptions.TryRemove(subscription.Id, out _);
        subscription.Close();
        if (!IsConnected || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(2)
            );
            await SendCore<JsonElement>(
                "event.unsubscribe",
                new
                {
                    subscriptionId = subscription.Id
                },
                timeout.Token
            );
        }
        catch (Exception ex)when (ex is IOException or OperationRejectedException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}

/// <summary>单订阅一个消费者；消费变慢时断线重同步，不在后台无限积累状态或静默丢终态。</summary>
public sealed class HostSubscription : IAsyncDisposable
{
    private readonly HostClient owner;
    private readonly Channel<HostEvent> events = Channel.CreateBounded<HostEvent>(128);
    private long sequence = -1;
    private int closed;
    internal HostSubscription(HostClient owner, string id, string kind, string subject)
    {
        this.owner = owner;
        Id = id;
        Kind = kind;
        SubjectId = subject;
    }

    public string Id { get; }
    public string Kind { get; }
    public string SubjectId { get; }
    public long LastSequence => Volatile.Read(ref sequence);
    internal bool IsClosed => Volatile.Read(ref closed) != 0;

    // 背压断开后使用权威快照覆盖未消费缓冲，已交付水位保留；不在慢消费者处无限排队。
    internal void PrepareResync()
    {
        while (events.Reader.TryRead(out _))
        {
        }
    }

    internal bool Enqueue(HostEvent value) => IsClosed || events.Writer.TryWrite(value);
    internal void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0)
        {
            events.Writer.TryComplete();
        }
    }

    public async IAsyncEnumerable<HostEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!IsClosed)
        {
            while (events.Reader.TryRead(out var item))
            {
                if (item.HostId != owner.HostId || item.Sequence <= LastSequence)
                {
                    continue;
                }

                Volatile.Write(ref sequence, item.Sequence);
                yield return item;
            }

            ct.ThrowIfCancellationRequested();
            if (!owner.IsConnected)
            {
                await owner.RecoverSubscriptionConnectionAsync(ct);
            }

            // 一个等待由当前消费者持有；定时检查连接时复用它，避免积累等待任务。
            var ready = events.Reader.WaitToReadAsync(ct)
                .AsTask();
            while (!ready.IsCompleted && !IsClosed)
            {
                await Task.WhenAny(
                    ready,
                    Task.Delay(250, ct)
                );
                ct.ThrowIfCancellationRequested();
                if (!owner.IsConnected)
                {
                    await owner.RecoverSubscriptionConnectionAsync(ct);
                }
            }

            if (!await ready)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync() => new(
        owner.RemoveSubscriptionAsync(this)
    );
}
