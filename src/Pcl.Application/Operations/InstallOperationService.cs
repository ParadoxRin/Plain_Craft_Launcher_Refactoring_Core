using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Application.Operations;
// 业务记录由 Application 定义；单文件同时持久化去重键、状态与 receipt，不存在两个文件之间的受理窗口。
public sealed record StoredOperation(
    int FormatVersion,
    string Key,
    string Fingerprint,
    OperationSnapshot Snapshot,
    InstallReceipt? Receipt = null
);
public sealed class InstallOperationService : IVersionInstallOperations
{
    private readonly object gate = new();
    private readonly IOperationStore store;
    private readonly IOperationInstaller installer;
    private readonly SemaphoreSlim? executionLane;
    private readonly Func<bool>? gameRunning;
    private readonly IOperationJournal? journal;
    private readonly IOperationObserver? observer;
    private readonly InstallationTarget target;
    private readonly Dictionary<string, StoredOperation> rows = new();
    private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> cancellations = new();
    private readonly Dictionary<string, TaskCompletionSource<OperationSnapshot>> completions = new();
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true
        }
    );
    private readonly Task worker;
    private readonly List<Task> cancellationSignals = new();
    private bool stopping;
    private volatile Exception? persistenceFailure;
    // 只能由持有独占存储 lease 的组合根构造；先恢复旧记录，再启动有所有者的单 worker。
    public InstallOperationService(
        IOperationStore store,
        IOperationInstaller installer,
        InstallationTarget target,
        SemaphoreSlim? executionLane = null,
        Func<bool>? gameRunning = null,
        IOperationJournal? journal = null,
        IOperationObserver? observer = null
    )
    {
        this.store = store;
        this.installer = installer;
        this.target = target;
        this.executionLane = executionLane;
        this.gameRunning = gameRunning;
        this.journal = journal;
        this.observer = observer;
        foreach (var bytes in store.ReadAll())
        {
            var row = JsonSerializer.Deserialize<StoredOperation>(bytes) ?? throw new InvalidDataException("OPERATION_STORE_INVALID");
            ValidateStored(row);
            if (!rows.TryAdd(row.Snapshot.OperationId, row) || !keys.TryAdd(row.Key, row.Snapshot.OperationId))
            {
                throw new InvalidDataException("OPERATION_STORE_INVALID");
            }

            if (!row.Snapshot.Terminal)
            {
                Save(
                    row with
                    {
                        Snapshot = row.Snapshot with
                        {
                            State = "interrupted",
                            Progress = null,
                            ErrorCode = "HOST_INTERRUPTED",
                            Sequence = row.Snapshot.Sequence + 1,
                            Updated = DateTimeOffset.UtcNow
                        }
                    }
                );
            }
        }

        worker = RunAsync();
    }

    // 受理只做有限本地校验、去重、持久化和排队；远程元数据与求解均属于后台操作。
    public Task<OperationAcceptance> AcceptAsync(InstallCommand command, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (persistenceFailure is not null)
            {
                throw new OperationRejectedException("STORE_UNAVAILABLE");
            }

            if (stopping)
            {
                throw new OperationRejectedException("APPLICATION_STOPPING");
            }

            Validate(command, false);
            var fingerprint = Fingerprint(command);
            if (keys.TryGetValue(command.IdempotencyKey, out var existing))
            {
                var old = rows[existing];
                if (old.Fingerprint != fingerprint)
                {
                    throw new OperationRejectedException("IDEMPOTENCY_CONFLICT");
                }

                return Task.FromResult(
                    new OperationAcceptance(existing, old.Snapshot.State, true)
                );
            }

            if (command.Installation.Target != target)
            {
                throw new OperationRejectedException("UNSUPPORTED_TARGET");
            }

            if (command.ResumeOfOperationId is { } parent)
            {
                if (!rows.TryGetValue(parent, out var old) || old.Snapshot.State is not ("cancelled" or "failed" or "interrupted"))
                {
                    throw new OperationRejectedException("RESUME_NOT_ALLOWED");
                }

                if (old.Snapshot.Request != command.Installation || old.Receipt is null || old.Receipt.OperationId != parent || (command.Installation.Loader != "vanilla" && old.Receipt.LoaderPin is null && old.Receipt.Loader != "vanilla"))
                {
                    throw new OperationRejectedException("RESUME_UNSUPPORTED");
                }
            }

            // 当前旧安装链共享索引与缓存，先采用数据目录级串行；未迁移修复/删除入口不冒充受此锁管理。
            if (rows.Values.Count(
                x => !x.Snapshot.Terminal
            ) >= 16)
            {
                throw new OperationRejectedException("QUEUE_FULL");
            }

            if (rows.Values.Count >= 4096)
            {
                throw new OperationRejectedException("OPERATION_RETENTION_LIMIT");
            }

            var id = "op-" + Guid.NewGuid()
                .ToString("N");
            var snapshot = new OperationSnapshot(
                id,
                "queued",
                "accepted",
                1,
                DateTimeOffset.UtcNow,
                command.Installation,
                command.ResumeOfOperationId
            );
            var row = new StoredOperation(1, command.IdempotencyKey, fingerprint, snapshot);
            Save(row);
            keys.Add(command.IdempotencyKey, id);
            cancellations.Add(
                id,
                new()
            );
            completions.Add(
                id,
                new(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            if (!queue.Writer.TryWrite(id))
            {
                throw new InvalidOperationException("OPERATION_QUEUE_CLOSED");
            }

            return Task.FromResult(
                new OperationAcceptance(id, "queued", false)
            );
        }
    }

    public Task<OperationSnapshot?> GetAsync(string id, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(
                rows.GetValueOrDefault(id)?.Snapshot
            );
        }
    }

    public Task<IReadOnlyList<OperationSnapshot>> ListAsync(CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<OperationSnapshot>>(
                rows.Values.Select(
                    x => x.Snapshot
                )
                    .OrderByDescending(
                        x => x.Updated
                    )
                    .ToArray()
            );
        }
    }

    // 先持久化 cancelling，再发信号；返回取消意图，不谎称工作已经停止。
    public Task<OperationSnapshot> CancelAsync(string id, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            var row = Find(id);
            if (!row.Snapshot.Terminal && row.Snapshot.State != "cancelling")
            {
                Update(row, "cancelling", row.Snapshot.Stage);
                SignalCancellation(cancellations[id]);
            }

            return Task.FromResult(rows[id].Snapshot);
        }
    }

    public Task<OperationSnapshot> WaitAsync(string id, CancellationToken wait = default)
    {
        lock (gate)
        {
            var row = Find(id);
            return row.Snapshot.Terminal
                ? Task.FromResult(row.Snapshot)
                : completions[id].Task.WaitAsync(wait);
        }
    }

    // 正常退出停止受理，明确取消并等待 worker；预算由组合根施加，超时不提前释放 store 或依赖。
    public Task StopAsync() => FinishAsync(true);
    public Task DrainAsync() => FinishAsync(false);
    private Task FinishAsync(bool cancel)
    {
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                foreach (var row in rows.Values.Where(
                    x => !x.Snapshot.Terminal
                )
                    .ToArray())
                {
                    if (!cancel)
                    {
                        continue;
                    }

                    if (persistenceFailure is null)
                    {
                        try
                        {
                            CancelAsync(row.Snapshot.OperationId);
                        }
                        catch (OperationRejectedException ex)when (ex.Code == "STORE_UNAVAILABLE")
                        {
                            if (cancellations.TryGetValue(row.Snapshot.OperationId, out var pending))
                            {
                                SignalCancellation(pending);
                            }
                        }
                    }
                    else if (cancellations.TryGetValue(row.Snapshot.OperationId, out var cancellation))
                    {
                        SignalCancellation(cancellation);
                    }
                }

                queue.Writer.TryComplete();
            }

            return Task.WhenAll(
                worker,
                Task.WhenAll(cancellationSignals)
            );
        }
    }

    // CancelAsync 的回调不在持久化锁或 UI 线程上同步执行；信号任务始终由 runner 持有并在退出时排空。
    private void SignalCancellation(CancellationTokenSource source)
    {
        cancellationSignals.Add(
            ObserveSignalAsync(
                source.CancelAsync()
            )
        );
    }

    private static async Task ObserveSignalAsync(Task signal)
    {
        try
        {
            await signal.ConfigureAwait(false);
        }
        catch (Exception)
        { /* 回调异常已被观察；操作本身仍以安装器的真实退出结果为准。 */
        }
    }

    private async Task RunAsync()
    {
        await foreach (var id in queue.Reader.ReadAllAsync()
            .ConfigureAwait(false))
        {
            await ExecuteAsync(id)
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteAsync(string id)
    {
        CancellationToken ct;
        StoredOperation row;
        lock (gate)
        {
            row = Find(id);
            ct = cancellations[id].Token;
        }

        bool laneHeld = false;
        try
        {
            if (executionLane is not null)
            {
                await executionLane.WaitAsync(ct)
                    .ConfigureAwait(false);
                laneHeld = true;
            }

            if (gameRunning?.Invoke() == true)
            {
                throw new OperationRejectedException("INSTANCE_BUSY");
            }

            if (persistenceFailure is not null)
            {
                throw new OperationRejectedException("STORE_UNAVAILABLE");
            }

            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                Update(
                    Find(id),
                    "running",
                    "planning"
                );
            }

            var request = row.Snapshot.Request;
            InstallReceipt? previous;
            lock (gate)
            {
                previous = row.Snapshot.ResumeOfOperationId is { } parent
                    ? Find(parent)
                        .Receipt
                    : null;
            }

            var result = await installer.InstallAsync(
                id,
                new(
                    request.GameVersion,
                    request.Loader,
                    request.Target.Os,
                    request.Target.Architecture,
                    request.Target.OsVersion
                ),
                previous,
                receipt =>
                {
                    lock (gate)
                    {
                        var current = Find(id);
                        Save(
                            current with
                            {
                                Receipt = receipt,
                                Snapshot = current.Snapshot with
                                {
                                    Stage = receipt.CommitStage,
                                    Sequence = current.Snapshot.Sequence + 1,
                                    Updated = DateTimeOffset.UtcNow
                                }
                            }
                        );
                    }
                },
                new ProgressSink(
                    value =>
                    {
                        lock (gate)
                        {
                            var current = Find(id);
                            // 查询只暴露已落盘水位；合并进度到每 250ms 一次，避免崩溃后 seq 倒退。
                            if (DateTimeOffset.UtcNow - current.Snapshot.Updated >= TimeSpan.FromMilliseconds(250))
                            {
                                Save(
                                    current with
                                    {
                                        Snapshot = current.Snapshot with
                                        {
                                            Progress = new(
                                                value.Stage,
                                                value.Completed,
                                                value.Total > 0
                                                    ? value.Total
                                                    : null,
                                                value.Bytes,
                                                value.TotalBytes > 0
                                                    ? value.TotalBytes
                                                    : null
                                            ),
                                            Sequence = current.Snapshot.Sequence + 1,
                                            Updated = DateTimeOffset.UtcNow
                                        }
                                    }
                                );
                            }
                        }
                    }
                ),
                ct
            )
                .ConfigureAwait(false);
            lock (gate)
            {
                // 安装器已提交即 succeeded，即使最后一刻收到了取消请求。
                var current = Find(id);
                Save(
                    current with
                    {
                        Snapshot = current.Snapshot with
                        {
                            State = "succeeded",
                            Stage = "committed",
                            Result = new(result.Id, result.Name, result.GameVersion, result.Loader, result.GameDirectory),
                            Sequence = current.Snapshot.Sequence + 1,
                            Updated = DateTimeOffset.UtcNow
                        }
                    }
                );
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                var current = Find(id);
                var code = ex is OperationInstallException rejected
                    ? rejected.Code
                    : ex is OperationRejectedException rejection
                        ? rejection.Code
                        : ex switch
                        {
                            OperationCanceledException when ct.IsCancellationRequested => "CANCELLED",
                            OperationCanceledException => "TIMEOUT",
                            HttpRequestException => "NETWORK_ERROR",
                            UnauthorizedAccessException => "ACCESS_DENIED",
                            InvalidDataException => "RECEIPT_OR_METADATA_INVALID",
                            NotSupportedException => "UNSUPPORTED",
                            IOException => "IO_ERROR",
                            _ => "INSTALL_FAILED"
                        };
                try
                {
                    Update(
                        current,
                        code == "CANCELLED"
                            ? "cancelled"
                            : "failed",
                        current.Snapshot.Stage,
                        code
                    );
                }
                catch (Exception failure)
                {
                    persistenceFailure = failure;
                    stopping = true;
                    queue.Writer.TryComplete();
                }
            }
        }
        finally
        {
            if (laneHeld)
            {
                executionLane!.Release();
            }

            lock (gate)
            {
                // 存储失败不伪造持久终态；等待者收到明确失败，重启后依旧将非终态认定 interrupted。
                if (persistenceFailure is null)
                {
                    completions[id].TrySetResult(
                        Find(id)
                            .Snapshot
                    );
                }
                else
                {
                    completions[id].TrySetException(
                        new OperationRejectedException("STORE_UNAVAILABLE")
                    );
                    // 即使客户端从未等待，也由 runner 观察失败；之后调用 WaitAsync 仍收到相同稳定错误。
                    _ = completions[id].Task.Exception;
                }

                cancellations[id].Dispose();
                cancellations.Remove(id);
            }
        }
    }

    private StoredOperation Find(string id) => rows.GetValueOrDefault(id) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
    private void Update(StoredOperation row, string state, string stage, string? error = null) => Save(
        row with
        {
            Snapshot = row.Snapshot with
            {
                State = state,
                Stage = stage,
                ErrorCode = error,
                Updated = DateTimeOffset.UtcNow,
                Sequence = row.Snapshot.Sequence + 1
            }
        }
    );
    private void Save(StoredOperation row)
    {
        try
        {
            store.Save(
                row.Snapshot.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(row)
            );
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            persistenceFailure = ex;
            throw new OperationRejectedException("STORE_UNAVAILABLE");
        }

        rows[row.Snapshot.OperationId] = row;
        try
        {
            observer?.Changed(
                new(
                    row.Snapshot.OperationId,
                    row.Snapshot.Sequence,
                    row.Snapshot.State,
                    row.Snapshot.Stage,
                    JsonSerializer.SerializeToUtf8Bytes(
                        row.Snapshot,
                        new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        }
                    )
                )
            );
        }
        catch (Exception)
        {
        }

        try
        {
            journal?.Write(
                new(
                    row.Snapshot.OperationId,
                    row.Snapshot.Sequence,
                    row.Snapshot.State == "failed"
                        ? "error"
                        : "info",
                    row.Snapshot.State + ":" + row.Snapshot.Stage
                )
            );
        }
        catch (Exception)
        { /* 诊断不可使受理或已完成安装丢失真实终态。 */
        }
    }

    private void Validate(InstallCommand command, bool checkTarget = true)
    {
        if (command is null || command.Installation is null || command.Installation.Target is null || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 128 || command.IdempotencyKey.Any(
            c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'
        ))
        {
            throw new OperationRejectedException("INVALID_ARGUMENT");
        }

        try
        {
            Pcl.Core.Domain.GameVersionIdentity.Validate(command.Installation.GameVersion);
        }
        catch (Exception)
        {
            throw new OperationRejectedException("INVALID_ARGUMENT");
        }

        if (checkTarget && command.Installation.Target != target)
        {
            throw new OperationRejectedException("UNSUPPORTED_TARGET");
        }

        if (command.Installation.Loader is not ("vanilla" or "auto" or "fabric" or "quilt" or "forge" or "neoforge" or "optifine" or "liteloader"))
        {
            throw new OperationRejectedException("INVALID_ARGUMENT");
        }
    }

    private void ValidateStored(StoredOperation row)
    {
        Validate(
            new(row.Key, row.Snapshot.Request, row.Snapshot.ResumeOfOperationId),
            false
        );
        if (row.FormatVersion != 1 || row.Snapshot.Sequence < 1 || row.Fingerprint != Fingerprint(
            new(row.Key, row.Snapshot.Request, row.Snapshot.ResumeOfOperationId)
        ) || row.Snapshot.State is not ("queued" or "running" or "cancelling" or "succeeded" or "failed" or "cancelled" or "interrupted"))
        {
            throw new InvalidDataException("OPERATION_STORE_INVALID");
        }
    }

    private static string Fingerprint(InstallCommand command) => Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    new
                    {
                        method = "version.install",
                        command.Installation,
                        command.ResumeOfOperationId
                    }
                )
            )
        )
    );
    private sealed class ProgressSink(Action<InstallProgress> action) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => action(value);
    }
}
