using System.Text.Json;
using System.Security.Cryptography;
using System.Threading.Channels;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Application.Pages;
public sealed record StoredPage(int FormatVersion, string ExecutionId, string Fingerprint, PageSnapshot Snapshot);
/// <summary>页面长任务统一受理：落盘后排队，持有 worker、取消源与终态；断线不取消执行。</summary>
public sealed class PageOperationService : IPageOperations
{
    private readonly object gate = new();
    private readonly IOperationStore store;
    private readonly IPageExecutor executor;
    private readonly SemaphoreSlim lane;
    private readonly IOperationObserver? observer;
    private readonly Dictionary<string, StoredPage> rows = new();
    private readonly Dictionary<string, string> keys = new();
    private readonly Dictionary<string, CancellationTokenSource> tokens = new();
    private readonly Dictionary<string, TaskCompletionSource<PageSnapshot>> completions = new();
    private readonly List<Task> signals = new();
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true
        }
    );
    private readonly Task worker;
    private bool stopping, storeFailed;
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public PageOperationService(
        IOperationStore store,
        IPageExecutor executor,
        SemaphoreSlim lane,
        IOperationObserver? observer = null
    )
    {
        this.store = store;
        this.executor = executor;
        this.lane = lane;
        this.observer = observer;
        foreach (var bytes in store.ReadAll())
        {
            var row = JsonSerializer.Deserialize<StoredPage>(bytes) ?? throw new InvalidDataException("PAGE_STORE_INVALID");
            Validate(row.Snapshot.Command);
            if (row.FormatVersion != 1 || row.Fingerprint != Fingerprint(row.Snapshot.Command) || !ValidId(row.ExecutionId) || !ValidId(row.Snapshot.OperationId) || row.Snapshot.State is not ("queued" or "running" or "cancelling" or "succeeded" or "failed" or "cancelled" or "interrupted") || !keys.TryAdd(
                Key(row.Snapshot.Command),
                row.Snapshot.OperationId
            ))
            {
                throw new InvalidDataException("PAGE_STORE_INVALID");
            }

            rows.Add(row.Snapshot.OperationId, row);
            if (!row.Snapshot.Terminal)
            {
                Save(
                    row with
                    {
                        Snapshot = row.Snapshot with
                        {
                            State = "interrupted",
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

    public string ExecutionId(string id)
    {
        lock (gate)
        {
            return Find(id)
                .ExecutionId;
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return rows.Values.Any(
                    x => !x.Snapshot.Terminal
                );
            }
        }
    }

    // 受理仅校验有界输入和持久化，不在返回操作 ID 前访问目录、网络或计算文件指纹。
    public Task<OperationAcceptance> AcceptAsync(PageCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Validate(command);
        lock (gate)
        {
            if (stopping || storeFailed)
            {
                throw new OperationRejectedException(
                    storeFailed
                        ? "STORE_UNAVAILABLE"
                        : "APPLICATION_STOPPING"
                );
            }

            var fingerprint = Fingerprint(command);
            var key = Key(command);
            if (keys.TryGetValue(key, out var prior))
            {
                var old = Find(prior);
                if (old.Fingerprint != fingerprint)
                {
                    throw new OperationRejectedException("IDEMPOTENCY_CONFLICT");
                }

                return Task.FromResult(
                    new OperationAcceptance(prior, old.Snapshot.State, true)
                );
            }

            if (rows.Count >= 4096 || rows.Values.Count(
                x => !x.Snapshot.Terminal
            ) >= 16)
            {
                throw new OperationRejectedException("QUEUE_OR_RETENTION_LIMIT");
            }

            var id = "op-" + Guid.NewGuid()
                .ToString("N");
            var execution = id;
            if (command.ResumeOfOperationId is { } parentId)
            {
                var parent = Find(parentId);
                if (parent.Snapshot.State is not ("interrupted" or "cancelled" or "failed") || Intent(parent.Snapshot.Command) != Intent(command))
                {
                    throw new OperationRejectedException("RESUME_INPUT_MISMATCH");
                }

                execution = parent.ExecutionId;
                if (rows.Values.Any(
                    x => x.ExecutionId == execution && !x.Snapshot.Terminal
                ))
                {
                    throw new OperationRejectedException("RESUME_ALREADY_RUNNING");
                }
            }

            var row = new StoredPage(
                1,
                execution,
                fingerprint,
                new(id, "queued", "accepted", 1, DateTimeOffset.UtcNow, command)
            );
            Save(row);
            keys.Add(key, id);
            tokens.Add(
                id,
                new()
            );
            completions.Add(
                id,
                new(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            queue.Writer.TryWrite(id);
            return Task.FromResult(
                new OperationAcceptance(id, "queued", false)
            );
        }
    }

    public Task<PageSnapshot> GetAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(
                Find(id)
                    .Snapshot
            );
        }
    }

    public Task<IReadOnlyList<PageSnapshot>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PageSnapshot>>(
                rows.Values.Select(
                    x => x.Snapshot
                )
                    .OrderBy(
                        x => x.Updated
                    )
                    .ToArray()
            );
        }
    }

    public Task<PageSnapshot> WaitAsync(string id, CancellationToken ct = default)
    {
        lock (gate)
        {
            var row = Find(id);
            return row.Snapshot.Terminal
                ? Task.FromResult(row.Snapshot)
                : completions[id].Task.WaitAsync(ct);
        }
    }

    public Task<PageSnapshot> CancelAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            var row = Find(id);
            if (!row.Snapshot.Terminal && row.Snapshot.State != "cancelling")
            {
                Change(id, "cancelling", "cancelling");
                signals.Add(
                    tokens[id].CancelAsync()
                );
            }

            return Task.FromResult(
                Find(id)
                    .Snapshot
            );
        }
    }

    // 停止等待所有已拥有任务；取消回调由 CancelAsync 异步驱动，不在存储锁内同步执行。
    public Task StopAsync() => FinishAsync(true);
    public Task DrainAsync() => FinishAsync(false);
    private Task FinishAsync(bool cancel)
    {
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                if (cancel)
                {
                    foreach (var pair in tokens.ToArray())
                    {
                        signals.Add(
                            pair.Value.CancelAsync()
                        );
                    }
                }

                queue.Writer.TryComplete();
            }

            return Task.WhenAll(
                worker,
                Task.WhenAll(signals)
            );
        }
    }

    private async Task RunAsync()
    {
        await foreach (var id in queue.Reader.ReadAllAsync())
        {
            CancellationToken ct;
            StoredPage row;
            lock (gate)
            {
                ct = tokens[id].Token;
                row = Find(id);
            }

            bool held = false;
            try
            {
                await lane.WaitAsync(ct);
                held = true;
                ct.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (storeFailed)
                    {
                        throw new OperationRejectedException("STORE_UNAVAILABLE");
                    }

                    Change(id, "running", "executing");
                }

                var c = row.Snapshot.Command;
                var bytes = await executor.ExecuteAsync(
                    row.ExecutionId,
                    new(
                        c.Method,
                        c.InstanceId,
                        c.GameVersion,
                        c.Loader,
                        c.Version,
                        c.DisableMods,
                        c.SelectionId,
                        c.PlanId,
                        c.PayloadJson
                    ),
                    new ProgressSink(
                        value => Report(id, value)
                    ),
                    ct
                );
                if (bytes.Length > 524288)
                {
                    throw new OperationRejectedException("PAGE_RESULT_TOO_LARGE");
                }

                using var doc = JsonDocument.Parse(bytes);
                lock (gate)
                {
                    var latest = Find(id);
                    Save(
                        latest with
                        {
                            Snapshot = latest.Snapshot with
                            {
                                State = "succeeded",
                                Stage = "completed",
                                Result = doc.RootElement.Clone(),
                                Sequence = latest.Snapshot.Sequence + 1,
                                Updated = DateTimeOffset.UtcNow
                            }
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                var code = ex switch
                {
                    OperationCanceledException when ct.IsCancellationRequested => "CANCELLED",
                    OperationInstallException failure => failure.Code,
                    OperationRejectedException failure => failure.Code,
                    HttpRequestException => "NETWORK_ERROR",
                    InvalidDataException => "INVALID_DATA",
                    _ => "PAGE_EXECUTION_FAILED"
                };
                lock (gate)
                {
                    var latest = Find(id);
                    var failed = latest with
                    {
                        Snapshot = latest.Snapshot with
                        {
                            State = code == "CANCELLED"
                                ? "cancelled"
                                : "failed",
                            Stage = "finished",
                            ErrorCode = code,
                            Sequence = latest.Snapshot.Sequence + 1,
                            Updated = DateTimeOffset.UtcNow
                        }
                    };
                    try
                    {
                        Save(failed);
                    }
                    catch
                    {
                        // 存储失效时只声明内存中的中断状态，仍通知等待者，拒绝之后的新写入。
                        rows[id] = failed with
                        {
                            Snapshot = failed.Snapshot with
                            {
                                State = "interrupted",
                                ErrorCode = "STORE_UNAVAILABLE"
                            }
                        };
                        Notify(rows[id]);
                    }
                }
            }
            finally
            {
                if (held)
                {
                    lane.Release();
                }

                lock (gate)
                {
                    completions[id].TrySetResult(
                        Find(id)
                            .Snapshot
                    );
                    tokens.Remove(id, out var token);
                    token?.Dispose();
                }
            }
        }
    }

    private void Report(string id, InstallProgress progress)
    {
        lock (gate)
        {
            var row = Find(id);
            if (row.Snapshot.Terminal)
            {
                return;
            }

            Save(
                row with
                {
                    Snapshot = row.Snapshot with
                    {
                        Stage = progress.Stage,
                        Progress = new(
                            progress.Stage,
                            progress.Completed,
                            progress.Total,
                            progress.Bytes,
                            progress.TotalBytes
                        ),
                        Sequence = row.Snapshot.Sequence + 1,
                        Updated = DateTimeOffset.UtcNow
                    }
                }
            );
        }
    }

    private void Change(string id, string state, string stage)
    {
        var row = Find(id);
        Save(
            row with
            {
                Snapshot = row.Snapshot with
                {
                    State = state,
                    Stage = stage,
                    Sequence = row.Snapshot.Sequence + 1,
                    Updated = DateTimeOffset.UtcNow
                }
            }
        );
    }

    private void Save(StoredPage row)
    {
        try
        {
            store.Save(
                row.Snapshot.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(row)
            );
        }
        catch
        {
            storeFailed = true;
            throw new OperationRejectedException("STORE_UNAVAILABLE");
        }

        rows[row.Snapshot.OperationId] = row;
        Notify(row);
    }

    private void Notify(StoredPage row)
    {
        try
        {
            observer?.Changed(
                new(
                    row.Snapshot.OperationId,
                    row.Snapshot.Sequence,
                    row.Snapshot.State,
                    row.Snapshot.Stage,
                    JsonSerializer.SerializeToUtf8Bytes(row.Snapshot, Wire)
                )
            );
        }
        catch
        { /* 观察故障不能改写状态事实。 */
        }
    }

    private StoredPage Find(string id) => rows.GetValueOrDefault(id) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
    private static string Key(PageCommand c) => c.Method + ":" + c.IdempotencyKey;
    private static string Intent(PageCommand c) => JsonSerializer.Serialize(
        c with
        {
            IdempotencyKey = "",
            ResumeOfOperationId = null
        }
    );
    private static string Fingerprint(PageCommand c) => Convert.ToHexString(
        SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(c)
        )
    );
    private static bool ValidId(string value) => value.StartsWith("op-", StringComparison.Ordinal) && Guid.TryParseExact(value[3..], "N", out _);
    private static void Validate(PageCommand c)
    {
        if (c.PayloadJson is { } payload)
        {
            if (payload.Length > 65536)
            {
                throw new OperationRejectedException("WORKSPACE_ARGUMENT_LIMIT");
            }

            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    MaxDepth = 16
                }
            );
            void Inspect(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Equals("CurseForgeApiKey", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("accessToken", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("refreshToken", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("password", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new OperationRejectedException("SECRET_IN_PERSISTED_COMMAND");
                        }

                        Inspect(property.Value);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in element.EnumerateArray())
                    {
                        Inspect(value);
                    }
                }
            }

            Inspect(document.RootElement);
        }

        if (!WorkspaceMethods.Known.Contains(c.Method) && c.Method is not ("component.confirmation" or "component.versions" or "component.preview" or "component.apply" or "artifact.list" or "artifact.download" or "artifact.notes" or "instance.list") || string.IsNullOrWhiteSpace(c.IdempotencyKey) || c.IdempotencyKey.Length > 128 || new[]
        {
            c.InstanceId,
            c.GameVersion,
            c.Loader,
            c.Version,
            c.SelectionId,
            c.PlanId,
            c.ResumeOfOperationId
        }.Any(
            x => x?.Length > 256 || x?.Contains('\0') == true
        ))
        {
            throw new OperationRejectedException("PAGE_COMMAND_INVALID");
        }
    }

    private sealed class ProgressSink(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
