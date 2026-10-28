using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BoundedNdjson = Pcl.Protocol.NdjsonFrames;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;
// 当前用户私有管道支持重连；CLI 保留 stdio。每次连接重新分配请求前缀，迟到响应只完成原请求。
public sealed partial class HostClient : IAsyncDisposable
{
    private Process? host;
    private string requestPrefix = Guid.NewGuid()
        .ToString("N");
    private long requestSequence;
    private int disconnected;
    private volatile bool ready;
    private readonly bool reconnectable;
    private readonly string executable, assembly, dataRoot;
    private readonly Pcl.Core.Contracts.Sessions.ClientIdentity identity = Pcl.Protocol.ProcessBirthIdentity.Current();
    private readonly SemaphoreSlim writeGate = new(1, 1), reconnectGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private Task reader = Task.CompletedTask, diagnostics = Task.CompletedTask;
    private Task bootstrapOutput = Task.CompletedTask;
    private Stream? input;
    private StreamWriter? writer;
    private readonly JsonSerializerOptions json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };
    public IVersionInstallOperations Installations { get; }
    public IGameLaunchOperations Launches { get; }
    public int HostPid => Attachment?.HostPid ?? host?.Id ?? 0;
    public string SafeDiagnostics { get; private set; } = "";
    public bool IsConnected => ready && Volatile.Read(ref disconnected) == 0;
    public Pcl.Core.Contracts.Sessions.SessionAttachment? Attachment { get; private set; }

    public event Action<Pcl.Core.Contracts.Sessions.SessionAttachment>? ConnectionRestored;
    public event Action? ConnectionLost;
    public bool CanReconnect => reconnectable;

    private readonly ConcurrentDictionary<string, HostSubscription> subscriptions = new();
    public HostClient(string dotnetExecutable, string hostAssembly, string dataRoot, bool reconnectable = false)
    {
        executable = dotnetExecutable;
        assembly = hostAssembly;
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.reconnectable = reconnectable;
        Installations = new RemoteInstall(this);
        Launches = new RemoteLaunch(this);
        if (!reconnectable)
        {
            host = StartHost(false);
            input = host.StandardOutput.BaseStream;
            writer = host.StandardInput;
            ready = true;
            reader = ReadAsync();
            diagnostics = DrainDiagnosticsAsync();
        }
        else
        {
            disconnected = 1;
        }
    }

    private Process StartHost(bool pipe)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (assembly.Length > 0)
        {
            start.ArgumentList.Add(assembly);
        }

        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(dataRoot);
        if (pipe)
        {
            start.ArgumentList.Add("--pipe");
        }

        return Process.Start(start) ?? throw new IOException("HOST_START_FAILED");
    }

    // 连接断开后复用原 Host；请求结果不确定时仅对有明确幂等保证的命令重放。
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (!reconnectable)
        {
            throw new OperationRejectedException("RECONNECT_UNSUPPORTED");
        }

        await EnsureConnectedAsync(ct);
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (ready)
        {
            return;
        }

        if (Volatile.Read(ref disposed) != 0)
        {
            throw new OperationRejectedException("CLIENT_CLOSED");
        }

        if (!reconnectable)
        {
            throw new OperationRejectedException("HOST_DISCONNECTED");
        }

        Pcl.Core.Contracts.Sessions.SessionAttachment? restored = null;
        await reconnectGate.WaitAsync(ct);
        try
        {
            if (ready)
            {
                return;
            }

            await reader;
            input?.Dispose();
            var path = Pcl.Protocol.PrivatePipePath.ForRoot(dataRoot);
            Pcl.Protocol.PrivatePipePath.SecureParent(path);
            System.IO.Pipes.NamedPipeClientStream? pipe = null;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            bool attemptedStart = false;
            while (true)
            {
                pipe = new(
                    ".",
                    path,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly
                );
                try
                {
                    await pipe.ConnectAsync(300, ct);
                    break;
                }
                catch (TimeoutException)
                {
                    pipe.Dispose();
                    if (!attemptedStart)
                    {
                        attemptedStart = true;
                        if (host is null || host.HasExited)
                        {
                            host?.Dispose();
                            host = StartHost(true);
                            diagnostics = DrainDiagnosticsAsync();
                            bootstrapOutput = DrainBootstrapOutputAsync();
                        }
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        throw new OperationRejectedException("HOST_CONNECT_TIMEOUT");
                    }
                }
            }

            try
            {
                var attachWriter = new StreamWriter(pipe, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                await attachWriter.WriteLineAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            protocolVersion = 1,
                            method = "session.attach",
                            @params = identity
                        },
                        json
                    )
                );
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(
                    TimeSpan.FromSeconds(5)
                );
                await using var frames = Pcl.Protocol.NdjsonFrames.ReadAsync(pipe, timeout.Token)
                    .GetAsyncEnumerator();
                if (!await frames.MoveNextAsync() || frames.Current.Error is not null)
                {
                    throw new OperationRejectedException("ATTACH_FAILED");
                }

                using var doc = JsonDocument.Parse(frames.Current.Data!);
                var envelope = doc.RootElement;
                if (envelope.TryGetProperty("error", out var error))
                {
                    throw new OperationRejectedException(
                        error.GetProperty("code")
                            .GetString()!
                    );
                }

                restored = envelope.GetProperty("result")
                    .Deserialize<Pcl.Core.Contracts.Sessions.SessionAttachment>(json)!;
                await writeGate.WaitAsync(ct);
                try
                {
                    input = pipe;
                    writer = attachWriter;
                    requestPrefix = Guid.NewGuid()
                        .ToString("N");
                    requestSequence = 0;
                    Volatile.Write(ref disconnected, 0);
                    reader = ReadAsync();
                }
                finally
                {
                    writeGate.Release();
                }

                var hello = await SendCore<HostHello>(
                    "host.hello",
                    new
                    {
                        minimumVersion = 1,
                        maximumVersion = 1
                    },
                    ct
                );
                if (hello.ProtocolVersion != 1 || hello.HostInstanceId != restored.HostId)
                {
                    throw new OperationRejectedException("PROTOCOL_INCOMPATIBLE");
                }

                HostId = hello.HostInstanceId;
                Capabilities = hello.Capabilities.ToHashSet(StringComparer.Ordinal);
                Attachment = restored;
                foreach (var subscription in subscriptions.Values)
                {
                    subscription.PrepareResync();
                    await RestoreSubscriptionAsync(subscription, ct);
                }

                ready = true;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
        finally
        {
            reconnectGate.Release();
        }

        if (restored is not null)
        {
            try
            {
                ConnectionRestored?.Invoke(restored);
            }
            catch (Exception)
            {
            }
        }
    }

    // 明确断开仅关闭传输，不请求取消安装、退出登录或终止游戏。用于系统连接恢复和诊断。
    public async Task DisconnectAsync()
    {
        ready = false;
        input?.Dispose();
        await reader;
    }

    private async Task DrainBootstrapOutputAsync()
    {
        var owned = host;
        if (owned is null)
        {
            return;
        }

        var buffer = new char[4096];
        try
        {
            while (await owned.StandardOutput.ReadAsync(buffer) > 0)
            {
                SafeDiagnostics = "HOST_BOOTSTRAP_OUTPUT_SUPPRESSED";
            }
        }
        catch (IOException)
        {
        }
    }

    public async Task<HostHello> HelloAsync(CancellationToken ct = default)
    {
        var hello = await Call<HostHello>(
            "host.hello",
            new
            {
                minimumVersion = 1,
                maximumVersion = 1
            },
            ct
        );
        if (hello.ProtocolVersion != 1 || hello.MaximumFrameBytes <= 0 || hello.MaximumFrameBytes > BoundedNdjson.MaximumBytes)
        {
            throw new OperationRejectedException("PROTOCOL_INCOMPATIBLE");
        }

        HostId = hello.HostInstanceId;
        Capabilities = hello.Capabilities.ToHashSet(StringComparer.Ordinal);
        return hello;
    }

    public Task<ResourceReference> OpenResourceAsync(string kind, string? operationId = null, CancellationToken ct = default) => Call<ResourceReference>(
        "blob.open",
        new
        {
            kind,
            operationId
        },
        ct
    );
    public Task<ResourceChunk> ReadResourceAsync(string id, long offset, int length, CancellationToken ct = default) => Call<ResourceChunk>(
        "blob.read",
        new
        {
            resourceId = id,
            offset,
            length
        },
        ct
    );
    public Task<ResourcePage> ReadPageAsync(string id, int offset, int count, CancellationToken ct = default) => Call<ResourcePage>(
        "blob.page",
        new
        {
            resourceId = id,
            offset,
            count
        },
        ct
    );
    public async Task CloseResourceAsync(string id, CancellationToken ct = default) => await Call<JsonElement>(
        "blob.close",
        new
        {
            resourceId = id
        },
        ct
    );
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await Call<JsonElement>(
            "host.shutdown",
            new { },
            ct
        );
        if (host is not null)
        {
            await host.WaitForExitAsync(ct);
        }

        await Task.WhenAll(reader, diagnostics, bootstrapOutput);
    }

    public async Task<T> Call<T>(string method, object parameters, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        try
        {
            return await SendCore<T>(method, parameters, ct);
        }
        catch (Exception ex)when (reconnectable && method != "host.shutdown" && ex is IOException or ObjectDisposedException or OperationRejectedException { Code: "HOST_DISCONNECTED" })
        {
            await EnsureConnectedAsync(ct);
            if (method == "desktop.acquire" && Attachment?.LegacyGrant is { } grant)
            {
                return JsonSerializer.SerializeToElement(
                    new
                    {
                        grant
                    },
                    json
                )
                    .Deserialize<T>(json)!;
            }

            if (method == "desktop.release" && Attachment?.LegacyGrant is null)
            {
                return JsonSerializer.SerializeToElement(
                    new
                    {
                        released = true
                    },
                    json
                )
                    .Deserialize<T>(json)!;
            }

            if (!RetrySafe(method))
            {
                throw new OperationRejectedException("RESPONSE_LOST_REQUERY");
            }

            return await SendCore<T>(method, parameters, ct);
        }
    }

    private static bool RetrySafe(string method) => method is "game.guidance" or "page.begin" or "page.get" or "page.cancel" or "host.hello" or "operation.get" or "game.get" or "game.details" or "operation.cancel" or "game.stop" or "account.snapshot" or "account.login.get" or "account.login.cancel" or "account.appearance.get" or "event.subscribe" or "event.unsubscribe" or "session.state" or "desktop.acquire" or "desktop.release" or "version.install" or "game.launch" or "account.login.begin" or "utility.get" or "utility.cancel" or "utility.begin";
    private async Task<T> SendCore<T>(string method, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new OperationRejectedException("CLIENT_CLOSED");
        }

        if (Volatile.Read(ref disconnected) != 0)
        {
            throw new OperationRejectedException("HOST_DISCONNECTED");
        }

        string? id = null;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await writeGate.WaitAsync(ct);
            try
            {
                if (requestSequence == long.MaxValue)
                {
                    throw new OperationRejectedException("CLIENT_REQUEST_SPACE_EXHAUSTED");
                }

                var next = requestSequence + 1;
                id = requestPrefix + ":" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        protocolVersion = 1,
                        id,
                        method,
                        @params = parameters
                    },
                    json
                );
                if (bytes.Length > BoundedNdjson.MaximumBytes)
                {
                    throw new OperationRejectedException("FRAME_TOO_LARGE");
                }

                pending[id] = completion;
                if (Volatile.Read(ref disconnected) != 0)
                {
                    throw new OperationRejectedException("HOST_DISCONNECTED");
                }

                requestSequence = next;
                await writer!.WriteLineAsync(
                    System.Text.Encoding.UTF8.GetString(bytes)
                );
                await writer.FlushAsync();
            }
            finally
            {
                writeGate.Release();
            }

            return (await completion.Task.WaitAsync(ct)).Deserialize<T>(json)!;
        }
        finally
        {
            if (id is not null)
            {
                pending.TryRemove(id, out _);
            }
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (var frame in BoundedNdjson.ReadAsync(input!))
            {
                if (frame.Error is not null)
                {
                    throw new OperationRejectedException(frame.Error);
                }

                using var document = JsonDocument.Parse(frame.Data!);
                var value = document.RootElement;
                if (!value.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1)
                {
                    throw new OperationRejectedException("PROTOCOL_INCOMPATIBLE");
                }

                if (value.TryGetProperty("method", out var notification) && notification.GetString() == "event.changed")
                {
                    var update = value.GetProperty("params")
                        .Deserialize<Pcl.Core.Contracts.Sessions.HostEvent>(json)!;
                    if (subscriptions.TryGetValue(update.SubscriptionId, out var subscription) && !subscription.Enqueue(update))
                    {
                        input?.Dispose();
                        throw new IOException("EVENT_CONSUMER_LAGGED");
                    }

                    continue;
                }

                if (!value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !pending.TryGetValue(
                    id.GetString()!,
                    out var completion
                ))
                {
                    continue;
                }

                if (value.TryGetProperty("error", out var error))
                {
                    completion.TrySetException(
                        new OperationRejectedException(
                            error.GetProperty("code")
                                .GetString()!
                        )
                    );
                    _ = completion.Task.Exception;
                }
                else
                {
                    completion.TrySetResult(
                        value.GetProperty("result")
                            .Clone()
                    );
                }
            }
        }
        catch (Exception)
        { /* 管道错误转为下方所有原等待者的稳定失败，不把原始协议或令牌写到诊断。 */
        }
        finally
        {
            ready = false;
            Volatile.Write(ref disconnected, 1);
            try
            {
                ConnectionLost?.Invoke();
            }
            catch (Exception)
            {
            }

            foreach (var completion in pending.Values)
            {
                completion.TrySetException(
                    new OperationRejectedException("HOST_DISCONNECTED")
                );
                _ = completion.Task.Exception;
            }
        }
    }

    private async Task DrainDiagnosticsAsync()
    {
        var owned = host;
        if (owned is null)
        {
            return;
        }

        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = await owned.StandardError.ReadAsync(buffer)) > 0)
            {
                // stderr 分块不能安全地逐块脱敏（令牌可能跨块）；仅记受控标记，不留原文。
                SafeDiagnostics = "HOST_STDERR_OUTPUT_SUPPRESSED";
            }
        }
        catch (Exception)
        {
        }
    }

    private int disposed;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in subscriptions.Values)
        {
            subscription.Close();
        }

        ready = false;
        if (reconnectable)
        {
            input?.Dispose();
            await reader;
        }
        else
        {
            try
            {
                host?.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        // 传输消失不是取消命令；不杀持有任务/游戏的独立 Host。
        if (host is not null && host.HasExited)
        {
            await Task.WhenAll(reader, diagnostics, bootstrapOutput);
            host.Dispose();
        }
        else if (host is not null)
        {
            cleanup = CleanupAsync(host);
        }
    }

    private Task? cleanup;
    private async Task CleanupAsync(Process owned)
    {
        try
        {
            await owned.WaitForExitAsync();
            await Task.WhenAll(reader, diagnostics, bootstrapOutput);
        }
        catch (Exception)
        {
        }
        finally
        {
            owned.Dispose();
        }
    }

    private sealed class RemoteInstall(HostClient owner) : IVersionInstallOperations, IVersionInstallEvents
    {
        public async IAsyncEnumerable<OperationSnapshot> WatchAsync(
            string id,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await using var subscription = await owner.SubscribeAsync("operation", id, ct);
            await foreach (var item in subscription.ReadAsync(ct))
            {
                var snapshot = item.Snapshot.Deserialize<OperationSnapshot>(owner.json)!;
                yield return snapshot;
                if (snapshot.Terminal)
                {
                    yield break;
                }
            }
        }

        public Task<OperationAcceptance> AcceptAsync(InstallCommand command, CancellationToken wait = default) => owner.Call<OperationAcceptance>("version.install", command, wait);
        public Task<OperationSnapshot?> GetAsync(string id, CancellationToken wait = default) => owner.Call<OperationSnapshot?>(
            "operation.get",
            new
            {
                operationId = id
            },
            wait
        );
        public Task<OperationSnapshot> CancelAsync(string id, CancellationToken wait = default) => owner.Call<OperationSnapshot>(
            "operation.cancel",
            new
            {
                operationId = id
            },
            wait
        );
        public async Task<OperationSnapshot> WaitAsync(string id, CancellationToken wait = default)
        {
            if (owner.Capabilities.Contains("event.subscribe.v1"))
            {
                await using var subscription = await owner.SubscribeAsync("operation", id, wait);
                await foreach (var item in subscription.ReadAsync(wait))
                {
                    var snapshot = item.Snapshot.Deserialize<OperationSnapshot>(owner.json)!;
                    if (snapshot.Terminal)
                    {
                        return snapshot;
                    }
                }

                throw new OperationRejectedException("SUBSCRIPTION_CLOSED");
            }

            while (true)
            {
                var row = await GetAsync(id, wait) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
                if (row.Terminal)
                {
                    return row;
                }

                await Task.Delay(250, wait);
            }
        }

        public async Task<IReadOnlyList<OperationSnapshot>> ListAsync(CancellationToken wait = default)
        {
            var resource = await owner.OpenResourceAsync("operations", ct: wait);
            try
            {
                var rows = new List<OperationSnapshot>();
                for (int offset = 0;; offset += 50)
                {
                    var page = await owner.ReadPageAsync(resource.ResourceId, offset, 50, wait);
                    foreach (var item in page.Items)
                    {
                        if (item.TryGetProperty("request", out _))
                        {
                            rows.Add(
                                item.Deserialize<OperationSnapshot>(owner.json)!
                            );
                        }
                    }

                    if (page.Eof)
                    {
                        return rows;
                    }
                }
            }
            finally
            {
                await owner.CloseResourceAsync(resource.ResourceId);
            }
        }
    }

    private sealed class RemoteLaunch(HostClient owner) : IGameLaunchOperations
    {
        public Task<OperationAcceptance> AcceptAsync(GameLaunchCommand command, CancellationToken wait = default) => owner.Call<OperationAcceptance>("game.launch", command, wait);
        public Task<GameLaunchSnapshot?> GetAsync(string id, CancellationToken wait = default) => owner.Call<GameLaunchSnapshot?>(
            "operation.get",
            new
            {
                operationId = id
            },
            wait
        );
        public Task<GameLaunchSnapshot> CancelAsync(string id, CancellationToken wait = default) => owner.Call<GameLaunchSnapshot>(
            "operation.cancel",
            new
            {
                operationId = id
            },
            wait
        );
        public async Task<GameLaunchSnapshot> WaitAsync(string id, CancellationToken wait = default)
        {
            if (owner.Capabilities.Contains("event.subscribe.v1"))
            {
                await using var subscription = await owner.SubscribeAsync("operation", id, wait);
                await foreach (var item in subscription.ReadAsync(wait))
                {
                    var snapshot = item.Snapshot.Deserialize<GameLaunchSnapshot>(owner.json)!;
                    if (snapshot.Terminal)
                    {
                        return snapshot;
                    }
                }

                throw new OperationRejectedException("SUBSCRIPTION_CLOSED");
            }

            while (true)
            {
                var row = await GetAsync(id, wait) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
                if (row.Terminal)
                {
                    return row;
                }

                await Task.Delay(250, wait);
            }
        }

        public Task<GameProcessSnapshot> GetGameAsync(string id, CancellationToken wait = default) => owner.Call<GameProcessSnapshot>(
            "game.get",
            new
            {
                gameProcessId = id
            },
            wait
        );
        public async Task StopGameAsync(string id, CancellationToken wait = default) => await owner.Call<JsonElement>(
            "game.stop",
            new
            {
                gameProcessId = id
            },
            wait
        );
    }
}
