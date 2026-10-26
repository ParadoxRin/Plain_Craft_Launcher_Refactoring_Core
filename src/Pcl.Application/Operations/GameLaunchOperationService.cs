using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Application.Operations;
public sealed record StoredLaunch(
    int FormatVersion,
    string Key,
    string Fingerprint,
    GameLaunchSnapshot Snapshot,
    string? InputRevision = null
);
// 启动成功只代表已创建进程；游戏退出状态独立查询。重启后不通过重放启动修复不确定结果。
public sealed class GameLaunchOperationService : IGameLaunchOperations
{
    private readonly object gate = new();
    private readonly IOperationStore store;
    private readonly IGameLaunchExecutor executor;
    private readonly SemaphoreSlim? executionLane;
    private readonly IOperationJournal? journal;
    private readonly IOperationObserver? observer;
    private readonly string? inputRevision;
    private readonly Dictionary<string, StoredLaunch> rows = new();
    private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> tokens = new();
    private readonly Dictionary<string, TaskCompletionSource<GameLaunchSnapshot>> completions = new();
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true
        }
    );
    private readonly List<Task> signals = new();
    private readonly Task worker;
    private bool stopping;
    private volatile bool storeFailed;
    public GameLaunchOperationService(
        IOperationStore store,
        IGameLaunchExecutor executor,
        SemaphoreSlim? executionLane = null,
        IOperationJournal? journal = null,
        string? inputRevision = null,
        IOperationObserver? observer = null
    )
    {
        this.store = store;
        this.executor = executor;
        this.executionLane = executionLane;
        this.journal = journal;
        this.observer = observer;
        this.inputRevision = inputRevision;
        foreach (var bytes in store.ReadAll())
        {
            var row = JsonSerializer.Deserialize<StoredLaunch>(bytes) ?? throw new InvalidDataException("LAUNCH_STORE_INVALID");
            Validate(row.Snapshot.Command);
            if (row.FormatVersion != 1 || row.Key != row.Snapshot.Command.IdempotencyKey || row.Fingerprint != Fingerprint(row.Snapshot.Command, row.InputRevision) || row.Snapshot.State is not ("queued" or "running" or "cancelling" or "succeeded" or "failed" or "cancelled" or "interrupted") || !keys.TryAdd(row.Key, row.Snapshot.OperationId))
            {
                throw new InvalidDataException("LAUNCH_STORE_INVALID");
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
                            ErrorCode = "LAUNCH_OUTCOME_UNKNOWN",
                            Sequence = row.Snapshot.Sequence + 1,
                            Updated = DateTimeOffset.UtcNow
                        }
                    }
                );
            }
        }

        worker = RunAsync();
    }

    // 本地受理、原子持久化与排队；认证和进程创建不能先于这个边界执行。
    public Task<OperationAcceptance> AcceptAsync(GameLaunchCommand command, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
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

            Validate(command);
            var fingerprint = Fingerprint(command, inputRevision);
            if (keys.TryGetValue(command.IdempotencyKey, out var oldId))
            {
                var old = rows[oldId];
                if (old.Fingerprint != fingerprint)
                {
                    throw new OperationRejectedException("IDEMPOTENCY_CONFLICT");
                }

                return Task.FromResult(
                    new OperationAcceptance(oldId, old.Snapshot.State, true)
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
            var row = new StoredLaunch(
                1,
                command.IdempotencyKey,
                fingerprint,
                new(id, "queued", "accepted", 1, DateTimeOffset.UtcNow, command),
                inputRevision
            );
            Save(row);
            keys.Add(command.IdempotencyKey, id);
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

    public Task<GameLaunchSnapshot?> GetAsync(string id, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(
                rows.GetValueOrDefault(id)?.Snapshot
            );
        }
    }

    // 只取消调用者的等待；后台取消源由 runner 持有，必须显式 CancelAsync。
    public Task<GameLaunchSnapshot> WaitAsync(string id, CancellationToken wait = default)
    {
        lock (gate)
        {
            var row = Find(id);
            return row.Snapshot.Terminal
                ? Task.FromResult(row.Snapshot)
                : completions[id].Task.WaitAsync(wait);
        }
    }

    // 先保存取消意图，再异步发送信号；已经创建的游戏不因启动操作取消而被杀死。
    public Task<GameLaunchSnapshot> CancelAsync(string id, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        lock (gate)
        {
            var row = Find(id);
            if (!row.Snapshot.Terminal && row.Snapshot.State != "cancelling")
            {
                Set(id, "cancelling", row.Snapshot.Stage);
                Signal(tokens[id]);
            }

            return Task.FromResult(
                Find(id)
                    .Snapshot
            );
        }
    }

    public object[] ListSnapshots()
    {
        lock (gate)
        {
            return rows.Values.Select(
                x => (object)x.Snapshot
            )
                .ToArray();
        }
    }

    public Task<GameProcessSnapshot> GetGameAsync(string id, CancellationToken wait = default)
    {
        wait.ThrowIfCancellationRequested();
        var game = executor.Get(id);
        return Task.FromResult(
            new GameProcessSnapshot(
                game.Id,
                game.Running
                    ? "running"
                    : "exited",
                game.ExitCode,
                game.Detached
            )
        );
    }

    public Task StopGameAsync(string id, CancellationToken wait = default) => executor.StopAsync(id, wait);
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

                    try
                    {
                        CancelAsync(row.Snapshot.OperationId);
                    }
                    catch (OperationRejectedException)
                    {
                        Signal(tokens[row.Snapshot.OperationId]);
                    }
                }

                queue.Writer.TryComplete();
            }

            // 这里只停止尚未创建游戏的启动操作，不因 Host 释放而杀死已运行游戏。
            return Task.WhenAll(
                worker,
                Task.WhenAll(signals)
            );
        }
    }

    // 单个有所有者的 worker 记录每次启动终态；spawning 的异常按不确定结果报告，绝不自动补启动。
    private async Task RunAsync()
    {
        await foreach (var id in queue.Reader.ReadAllAsync()
            .ConfigureAwait(false))
        {
            CancellationToken ct;
            lock (gate)
            {
                ct = tokens[id].Token;
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

                if (storeFailed)
                {
                    throw new OperationRejectedException("STORE_UNAVAILABLE");
                }

                ct.ThrowIfCancellationRequested();
                GameLaunchCommand command;
                lock (gate)
                {
                    Set(id, "running", "authorizing");
                    command = Find(id)
                        .Snapshot.Command;
                }

                var result = await executor.StartAsync(
                    new(
                        command.AccountId,
                        command.InstanceId,
                        command.Detached,
                        command.Guidance?.Culture ?? "zh-CN",
                        command.Guidance?.SponsorRead ?? false,
                        command.Guidance?.PurchaseRead ?? false,
                        command.Guidance?.Demo ?? false
                    ),
                    () =>
                    {
                        lock (gate)
                        {
                            Set(
                                id,
                                Find(id)
                                    .Snapshot.State,
                                "spawning"
                            );
                        }

                        ct.ThrowIfCancellationRequested();
                    },
                    ct
                )
                    .ConfigureAwait(false);
                lock (gate)
                {
                    var row = Find(id);
                    Save(
                        row with
                        {
                            Snapshot = row.Snapshot with
                            {
                                State = "succeeded",
                                Stage = "process-created",
                                Result = new(result.Id, result.Pid, result.Detached),
                                Sequence = row.Snapshot.Sequence + 1,
                                Updated = DateTimeOffset.UtcNow
                            }
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                var dependency = Pcl.Core.Application.Diagnostics.DependencyFailureMapper.From(ex);
                var code = dependency?.Code ?? (ex switch
                {
                    OperationCanceledException when ct.IsCancellationRequested => "CANCELLED",
                    LoginRequiredException => "LOGIN_REQUIRED",
                    OperationRejectedException e => e.Code,
                    OperationInstallException e => e.Code,
                    HttpRequestException => "NETWORK_ERROR",
                    InvalidDataException => "INVALID_DATA",
                    _ => "LAUNCH_FAILED"
                });
                lock (gate)
                {
                    if (Find(id)
                        .Snapshot.Stage == "spawning" && code != "CANCELLED")
                    {
                        code = "LAUNCH_OUTCOME_UNKNOWN";
                    }

                    try
                    {
                        Set(
                            id,
                            code == "CANCELLED"
                                ? "cancelled"
                                : "failed",
                            Find(id)
                                .Snapshot.Stage,
                            code,
                            dependency
                        );
                    }
                    catch (Exception)
                    {
                        storeFailed = true;
                        queue.Writer.TryComplete();
                        stopping = true;
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
                    if (storeFailed)
                    {
                        completions[id].TrySetException(
                            new OperationRejectedException("STORE_UNAVAILABLE")
                        );
                        _ = completions[id].Task.Exception;
                    }
                    else
                    {
                        completions[id].TrySetResult(
                            Find(id)
                                .Snapshot
                        );
                    }

                    tokens[id].Dispose();
                    tokens.Remove(id);
                }
            }
        }
    }

    private void Signal(CancellationTokenSource source) => signals.Add(
        Observe(
            source.CancelAsync()
        )
    );
    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private StoredLaunch Find(string id) => rows.GetValueOrDefault(id) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
    private void Set(
        string id,
        string state,
        string stage,
        string? error = null,
        Pcl.Core.Contracts.Diagnostics.DependencyDiagnostic? dependency = null
    )
    {
        var row = Find(id);
        Save(
            row with
            {
                Snapshot = row.Snapshot with
                {
                    State = state,
                    Stage = stage,
                    ErrorCode = error,
                    DependencyFailure = dependency,
                    Updated = DateTimeOffset.UtcNow,
                    Sequence = row.Snapshot.Sequence + 1
                }
            }
        );
    }

    // 去重键与结果同文档落盘，诊断另存；日志失败不得破坏已保存的受理事实。
    private void Save(StoredLaunch row)
    {
        try
        {
            store.Save(
                row.Snapshot.OperationId,
                JsonSerializer.SerializeToUtf8Bytes(row)
            );
            rows[row.Snapshot.OperationId] = row;
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            storeFailed = true;
            throw new OperationRejectedException("STORE_UNAVAILABLE");
        }

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
        {
        }
    }

    private static string Fingerprint(GameLaunchCommand command, string? revision = null)
    {
        object normalized = string.IsNullOrEmpty(revision)
            ? new
            {
                method = "game.launch",
                command.AccountId,
                command.InstanceId,
                command.Detached
            }

            : (object)new
            {
                method = "game.launch",
                command.AccountId,
                command.InstanceId,
                command.Detached,
                optionsRevision = revision
            };
        // Preserve old receipt fingerprints when no new guidance fields exist.
        if (command.Guidance is not null)
        {
            normalized = new
            {
                launch = normalized,
                guidance = command.Guidance
            };
        }

        return Convert.ToHexString(
            SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(normalized)
            )
        );
    }

    private static void Validate(GameLaunchCommand command)
    {
        if (command is null || !Valid(command.IdempotencyKey, 128) || !ValidReference(command.AccountId) || !ValidReference(command.InstanceId))
        {
            throw new OperationRejectedException("INVALID_ARGUMENT");
        }
    }

    private static bool ValidReference(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
    private static bool Valid(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.All(
        x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_' or '.' or '+'
    );
}
