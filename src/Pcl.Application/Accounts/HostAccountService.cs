using System.Text.Json;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Application.Accounts;
// Host 持有认证执行任务与所有会话；UI 只轮询短时提示和公开账户索引。
public sealed class HostAccountService(Pcl.Core.Ports.Accounts.IAccountBackend accounts, IOperationStore store)
{
    private sealed record Saved(int FormatVersion, string Key, bool Remember, LoginSnapshot Snapshot);
    private readonly object gate = new();
    private readonly Dictionary<string, Saved> rows = new();
    private readonly Dictionary<string, CancellationTokenSource> cancellations = new();
    private readonly Dictionary<string, Task> executions = new();
    private bool stopping;
    public bool IsBusy
    {
        get
        {
            lock (gate)
            {
                return executions.Values.Any(
                    x => !x.IsCompleted
                );
            }
        }
    }

    public void Restore()
    {
        lock (gate)
        {
            foreach (var bytes in store.ReadAll())
            {
                var saved = JsonSerializer.Deserialize<Saved>(bytes) ?? throw new InvalidDataException("ACCOUNT_STORE_INVALID");
                if (saved.FormatVersion != 1)
                {
                    throw new InvalidDataException("ACCOUNT_STORE_INVALID");
                }

                if (saved.Snapshot.State is not ("succeeded" or "failed" or "cancelled"))
                {
                    saved = saved with
                    {
                        Snapshot = new(saved.Snapshot.OperationId, "failed", ErrorCode: "LOGIN_INTERRUPTED")
                    };
                }

                rows.Add(saved.Snapshot.OperationId, saved);
                Save(saved);
            }
        }
    }

    public AccountSnapshot Snapshot()
    {
        var index = accounts.Read();
        return new(
            index.Accounts.Select(
                x => new AccountInfo(
                    x.Id,
                    x.Kind,
                    x.Name,
                    x.Uuid,
                    x.ClientId,
                    x.Remembered,
                    x.Server,
                    x.Identifier,
                    x.ServerName,
                    x.HasCredential
                )
            )
                .ToArray(),
            index.SelectedId
        );
    }

    public LoginSnapshot Begin(string key, bool remember)
    {
        lock (gate)
        {
            if (stopping)
            {
                throw new OperationRejectedException("APPLICATION_STOPPING");
            }

            if (key.Length is < 1 or > 128 || !key.All(
                c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'
            ))
            {
                throw new OperationRejectedException("INVALID_ARGUMENT");
            }

            var existing = rows.Values.FirstOrDefault(
                x => x.Key == key
            );
            if (existing is not null)
            {
                if (existing.Remember != remember)
                {
                    throw new OperationRejectedException("IDEMPOTENCY_CONFLICT");
                }

                return existing.Snapshot;
            }

            if (IsBusy || rows.Count >= 512)
            {
                throw new OperationRejectedException("LOGIN_BUSY_OR_RETENTION_LIMIT");
            }

            var id = "op-" + Guid.NewGuid()
                .ToString("N");
            var saved = new Saved(
                1,
                key,
                remember,
                new(id, "queued")
            );
            Save(saved);
            cancellations[id] = new();
            // 被字典持有并由 StopOwnedAsync 观察的后台 Task，避免系统钥匙串/网络同步前段阻塞协议读取。
            executions[id] = Task.Run(
                () => ExecuteAsync(id, remember, cancellations[id].Token)
            );
            return rows[id].Snapshot;
        }
    }

    private async Task ExecuteAsync(string id, bool remember, CancellationToken ct)
    {
        try
        {
            var accountId = await accounts.LoginAsync(
                remember,
                challenge =>
                {
                    lock (gate)
                    {
                        Save(
                            rows[id] with
                            {
                                Snapshot = new(
                                    id,
                                    "challenge",
                                    new(challenge.UserCode, challenge.VerificationUri, challenge.ExpiresIn)
                                )
                            }
                        );
                    }
                },
                ct
            )
                .ConfigureAwait(false);
            lock (gate)
            {
                Save(
                    rows[id] with
                    {
                        Snapshot = new(id, "succeeded", AccountId: accountId)
                    }
                );
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                Save(
                    rows[id] with
                    {
                        Snapshot = new(
                            id,
                            ct.IsCancellationRequested
                                ? "cancelled"
                                : "failed",
                            ErrorCode: ct.IsCancellationRequested
                                ? "CANCELLED"
                                : ex is HttpRequestException
                                    ? "NETWORK_ERROR"
                                    : "LOGIN_FAILED"
                        )
                    }
                );
            }
        }
    }

    public LoginSnapshot Get(string id)
    {
        lock (gate)
        {
            return rows.GetValueOrDefault(id)?.Snapshot ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
        }
    }

    public async Task CancelAsync(string id)
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            cancellations.TryGetValue(id, out cancellation);
        }

        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
        }
    }

    private Task? stoppingTask;
    public Task StopAsync()
    {
        lock (gate)
        {
            stopping = true;
            return stoppingTask ??= StopOwnedAsync(
                executions.Values.ToArray(),
                cancellations.Values.ToArray()
            );
        }
    }

    // 关闭是一个被 Host 持有的 Task；所有认证执行退出后才释放它们各自的 CTS，重复关闭不会再次释放。
    private static async Task StopOwnedAsync(Task[] work, CancellationTokenSource[] cancel)
    {
        try
        {
            // 即使某个取消回调失败，也必须等待所有执行 finally 后再释放 CTS。
            var signals = Task.WhenAll(
                cancel.Select(
                    x => x.CancelAsync()
                )
            );
            await Task.WhenAll(
                signals,
                Task.WhenAll(work)
            );
        }
        finally
        {
            foreach (var token in cancel)
            {
                token.Dispose();
            }
        }
    }

    public void Logout(string id, bool remove) => accounts.Logout(id, remove);
    private readonly Dictionary<string, AppearanceSnapshot> appearances = new();
    public AppearanceSnapshot BeginAppearance(string id, string action, string? value, byte[]? png, bool slim)
    {
        lock (gate)
        {
            if (stopping || IsBusy || appearances.Count >= 512)
            {
                throw new OperationRejectedException("ACCOUNT_OPERATION_BUSY");
            }

            var operationId = "op-" + Guid.NewGuid()
                .ToString("N");
            appearances[operationId] = new(operationId, "running");
            cancellations[operationId] = new();
            executions[operationId] = Task.Run(
                () => ExecuteAppearanceAsync(operationId, id, action, value, png, slim, cancellations[operationId].Token)
            );
            return appearances[operationId];
        }
    }

    public AppearanceSnapshot GetAppearance(string id)
    {
        lock (gate)
        {
            return appearances.GetValueOrDefault(id) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
        }
    }

    // 外部 HTTP 由 Host 持有并观察，协议读取循环不会被皮肤上传阻塞，关窗可发送明确取消。
    private async Task ExecuteAppearanceAsync(
        string operationId,
        string id,
        string action,
        string? value,
        byte[]? png,
        bool slim,
        CancellationToken ct
    )
    {
        try
        {
            var result = await AppearanceAsync(id, action, value, png, slim, ct)
                .ConfigureAwait(false);
            lock (gate)
            {
                appearances[operationId] = new(operationId, "succeeded", result as AccountAppearance);
            }
        }
        catch (Exception)
        {
            lock (gate)
            {
                appearances[operationId] = new(
                    operationId,
                    ct.IsCancellationRequested
                        ? "cancelled"
                        : "failed",
                    ErrorCode: ct.IsCancellationRequested
                        ? "CANCELLED"
                        : "ACCOUNT_APPEARANCE_FAILED"
                );
            }
        }
    }

    public async Task<object> AppearanceAsync(
        string id,
        string action,
        string? value,
        byte[]? png,
        bool slim,
        CancellationToken ct = default
    )
    {
        var look = await accounts.AppearanceAsync(id, action, value, png, slim, ct)
            .ConfigureAwait(false);
        return look is null
            ? new
            {
                completed = true
            }

            : new AccountAppearance(
                look.Id,
                look.Name,
                look.Skins.Select(
                    x => new SkinInfo(x.Id, x.State, x.Url, x.Variant)
                )
                    .ToArray(),
                look.Capes.Select(
                    x => new CapeInfo(x.Id, x.State, x.Url, x.Name)
                )
                    .ToArray()
            );
    }

    private void Save(Saved saved)
    {
        // 短时 UserCode 仅在内存中供 UI 查询；恢复记录不保存授权提示或任何令牌。
        store.Save(
            saved.Snapshot.OperationId,
            JsonSerializer.SerializeToUtf8Bytes(
                saved with
                {
                    Snapshot = saved.Snapshot with
                    {
                        Prompt = null
                    }
                }
            )
        );
        rows[saved.Snapshot.OperationId] = saved;
    }
}
