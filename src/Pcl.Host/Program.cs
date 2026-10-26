using Pcl.Core.Application.Pages;
using Pcl.Core.Adapters.Pages;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Application.Accounts;
using Pcl.Core.Application.Launch;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pcl.Core;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Adapters.Processes;
using Pcl.Core.Adapters.Protocol;
using Pcl.Core.Application.Operations;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.Host;

// 在任何库初始化前隔离 stdout；只有本地持有的协议 StreamWriter 可以向管道写入。
var output = new StreamWriter(
    Console.OpenStandardOutput()
)
{
    AutoFlush = true,
    NewLine = "\n"
};
Console.SetOut(TextWriter.Null);
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 32
};
bool connected = true, hello = false, requestedShutdown = false;
bool pipeMode = args.Contains("--pipe");
var hostId = Guid.NewGuid()
    .ToString("N");
var requests = new RequestIdentityTracker();
var resources = new Resources();
ProtocolConnection? activeConnection = null;
try
{
    if (args.Length is not (2 or 3) || args[0] != "--data-root" || args.Length == 3 && args[2] != "--pipe")
    {
        throw new InvalidDataException("HOST_ARGUMENTS_INVALID");
    }

    var paths = new AppPaths(args[1]);
    using var rootLease = new FileStream(
        Path.Combine(paths.Root, ".launcher.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None
    );
    bool persistent = args.Length == 3;
    using var pipeServer = persistent
        ? new PrivatePipeServer(paths.Root)
        : null;
    using var owner = new SessionOwner();
    var broker = new OperationEventBroker();
    await using var runtime = new HostRuntime(paths, broker);
    var source = runtime.Source;
    var accounts = runtime.Accounts;
    var accountService = runtime.AccountService;
    var executor = runtime.Executor;
    var target = runtime.Target;
    var installs = runtime.Installs;
    var pages = runtime.Pages;
    var pageExecutor = runtime.PageExecutor;
    var launches = runtime.Launches;
    var journal = runtime.Journal;
    try
    {
        do
        {
            connected = true;
            hello = false;
            requests = new RequestIdentityTracker();
            activeConnection = persistent
                ? await pipeServer!.AcceptAsync(
                    async identity =>
                    {
                        bool resumed = owner.Accept(identity);
                        return await SessionState(resumed);
                    }
                )
                : new ProtocolConnection(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    false
                );
            await using var connection = activeConnection;
            await using var subscriptions = new OperationSubscriptions(
                hostId,
                broker,
                Snapshot,
                value => Send(
                    new
                    {
                        protocolVersion = 1,
                        method = "event.changed",
                        @params = value
                    }
                ),
                connection.Abort
            );
            try
            {
                await foreach (var frame in BoundedNdjson.ReadAsync(connection.Input))
                {
                    if (frame.Error is { } frameError)
                    {
                        await Error(null, frameError);
                        continue;
                    }

                    string? id = null;
                    try
                    {
                        using var document = JsonDocument.Parse(
                            frame.Data!,
                            new JsonDocumentOptions
                            {
                                MaxDepth = 32
                            }
                        );
                        var message = document.RootElement;
                        id = message.GetProperty("id")
                            .GetString();
                        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                        {
                            throw new OperationRejectedException("REQUEST_ID_INVALID");
                        }

                        if (!requests.Accept(id))
                        {
                            throw new OperationRejectedException("REQUEST_ID_REUSED_OR_LIMIT");
                        }

                        if (message.GetProperty("protocolVersion")
                            .GetInt32() != 1)
                        {
                            throw new OperationRejectedException("PROTOCOL_INCOMPATIBLE");
                        }

                        var method = message.GetProperty("method")
                            .GetString();
                        var parameters = message.TryGetProperty("params", out var value)
                            ? value
                            : JsonSerializer.SerializeToElement(
                                new { }
                            );
                        if (method == "host.hello")
                        {
                            var minimum = parameters.GetProperty("minimumVersion")
                                .GetInt32();
                            var maximum = parameters.GetProperty("maximumVersion")
                                .GetInt32();
                            if (minimum > 1 || maximum < 1)
                            {
                                throw new OperationRejectedException("PROTOCOL_INCOMPATIBLE");
                            }

                            hello = true;
                            await Result(
                                id,
                                new HostHello(
                                    1,
                                    hostId,
                                    [
                                        "page.operations.v1",
                                        "workspace.operations.v1",
                                        "blob.file.v1",
                                        "version.install.v3",
                                        "game.launch.v2",
                                        "game.guidance.v1",
                                        "session.reconnect.v1",
                                        "event.subscribe.v1",
                                        "request.ordered-ids.v1",
                                        "operation.snapshot",
                                        "operation.cancel",
                                        "blob.read",
                                        "blob.page",
                                        "operation.log.snapshot",
                                        "desktop.contract-only.v1",
                                        "account.login.v1",
                                        "game.details"
                                    ],
                                    BoundedNdjson.MaximumBytes,
                                    target
                                )
                            );
                            continue;
                        }

                        if (!hello)
                        {
                            throw new OperationRejectedException("HANDSHAKE_REQUIRED");
                        }

                        if (method is ("version.install" or "game.launch") && accountService.IsBusy)
                        {
                            throw new OperationRejectedException("ACCOUNT_OPERATION_BUSY");
                        }

                        if (method is ("account.login.begin" or "account.logout" or "account.remove" or "account.appearance" or "account.appearance.begin") && ((await installs.ListAsync()).Any(
                            x => !x.Terminal
                        ) || launches.ListSnapshots()
                            .Cast<GameLaunchSnapshot>()
                            .Any(
                                x => !x.Terminal
                            ) || executor.HasRunning))
                        {
                            throw new OperationRejectedException("HOST_WRITER_ACTIVE");
                        }

                        bool linkPage = method == "page.begin" && parameters.TryGetProperty("method", out var pageMethod) && pageMethod.GetString()is "workspace.link.start" or "workspace.link.status" or "workspace.link.stop";
                        if (method == "page.begin" && ((executor.HasRunning && !linkPage) || accountService.IsBusy || (await installs.ListAsync()).Any(
                            x => !x.Terminal
                        ) || launches.ListSnapshots()
                            .Cast<GameLaunchSnapshot>()
                            .Any(
                                x => !x.Terminal
                            )))
                        {
                            throw new OperationRejectedException("HOST_WRITER_ACTIVE");
                        }

                        if (pages.IsBusy && method is ("version.install" or "game.launch" or "account.login.begin" or "account.logout" or "account.remove" or "account.appearance" or "account.appearance.begin"))
                        {
                            throw new OperationRejectedException("HOST_WRITER_ACTIVE");
                        }

                        object? result = method switch
                        {
                            "multiplayer.invitation" => pageExecutor.LinkInvitation(
                                parameters.GetProperty("id")
                                    .GetString()!
                            ),
                            "workspace.secret" => new
                            {
                                handle = pageExecutor.RememberSecret(
                                    parameters.GetProperty("value")
                                        .GetString()!
                                )
                            },
                            "page.begin" => await pages.AcceptAsync(
                                parameters.Deserialize<PageCommand>(json)!
                            ),
                            "page.get" => await pages.GetAsync(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "page.cancel" => await pages.CancelAsync(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "session.state" => await SessionState(true),
                            "event.subscribe" => subscriptions.Subscribe(
                                parameters.GetProperty("subscriptionId")
                                    .GetString()!,
                                parameters.GetProperty("kind")
                                    .GetString()!,
                                parameters.GetProperty("subjectId")
                                    .GetString()!,
                                parameters.GetProperty("afterSequence")
                                    .GetInt64()
                            ),
                            "event.unsubscribe" => Unsubscribe(
                                parameters.GetProperty("subscriptionId")
                                    .GetString()!
                            ),
                            "account.snapshot" => accountService.Snapshot(),
                            "desktop.snapshot" => pageExecutor.DesktopSnapshot(
                                accountService.Snapshot()
                            ),
                            "downloads.snapshot" => pageExecutor.DownloadSnapshot(),
                            "downloads.pause" => pageExecutor.PauseDownloads(
                                parameters.GetProperty("paused")
                                    .GetBoolean()
                            ),
                            "account.login.begin" => accountService.Begin(
                                parameters.GetProperty("key")
                                    .GetString()!,
                                parameters.GetProperty("remember")
                                    .GetBoolean()
                            ),
                            "account.login.get" => accountService.Get(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "account.login.cancel" => await CancelLogin(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "account.logout" => Logout(
                                parameters.GetProperty("accountId")
                                    .GetString()!,
                                false
                            ),
                            "account.remove" => Logout(
                                parameters.GetProperty("accountId")
                                    .GetString()!,
                                true
                            ),
                            "account.appearance.begin" => accountService.BeginAppearance(
                                parameters.GetProperty("accountId")
                                    .GetString()!,
                                parameters.GetProperty("action")
                                    .GetString()!,
                                parameters.TryGetProperty("value", out var appearanceValue)
                                    ? appearanceValue.GetString()
                                    : null,
                                parameters.TryGetProperty("png", out var appearancePng) && appearancePng.ValueKind == JsonValueKind.String
                                    ? appearancePng.GetBytesFromBase64()
                                    : null,
                                parameters.TryGetProperty("slim", out var appearanceSlim) && appearanceSlim.GetBoolean()
                            ),
                            "account.appearance.get" => accountService.GetAppearance(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "account.appearance.cancel" => await CancelLogin(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "account.appearance" => await accountService.AppearanceAsync(
                                parameters.GetProperty("accountId")
                                    .GetString()!,
                                parameters.GetProperty("action")
                                    .GetString()!,
                                parameters.TryGetProperty("value", out var av)
                                    ? av.GetString()
                                    : null,
                                parameters.TryGetProperty("png", out var png) && png.ValueKind == JsonValueKind.String
                                    ? png.GetBytesFromBase64()
                                    : null,
                                parameters.TryGetProperty("slim", out var slim) && slim.GetBoolean()
                            ),
                            "game.details" => executor.Details(
                                parameters.GetProperty("gameProcessId")
                                    .GetString()!
                            ),
                            "version.install" => await installs.AcceptAsync(
                                parameters.Deserialize<InstallCommand>(json)!
                            ),
                            "game.guidance" => executor.GetGuidance(
                                parameters.GetProperty("accountId")
                                    .GetString()!,
                                parameters.GetProperty("culture")
                                    .GetString()!
                            ),
                            "game.launch" => await launches.AcceptAsync(
                                parameters.Deserialize<GameLaunchCommand>(json)!
                            ),
                            "operation.get" => await GetOperation(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "operation.cancel" => await CancelOperation(
                                parameters.GetProperty("operationId")
                                    .GetString()!
                            ),
                            "game.get" => await launches.GetGameAsync(
                                parameters.GetProperty("gameProcessId")
                                    .GetString()!
                            ),
                            "game.stop" => await StopGame(
                                parameters.GetProperty("gameProcessId")
                                    .GetString()!
                            ),
                            "blob.open" => await OpenResource(parameters),
                            "blob.read" => resources.Read(
                                parameters.GetProperty("resourceId")
                                    .GetString()!,
                                parameters.GetProperty("offset")
                                    .GetInt64(),
                                parameters.GetProperty("length")
                                    .GetInt32()
                            ),
                            "blob.page" => resources.Page(
                                parameters.GetProperty("resourceId")
                                    .GetString()!,
                                parameters.GetProperty("offset")
                                    .GetInt32(),
                                parameters.GetProperty("count")
                                    .GetInt32()
                            ),
                            "blob.close" => CloseResource(
                                parameters.GetProperty("resourceId")
                                    .GetString()!
                            ),
                            "host.shutdown" => !executor.HasManagedRunning
                                ? new
                                {
                                    accepted = true
                                }

                                : throw new OperationRejectedException("MANAGED_GAME_RUNNING"),
                            _ => throw new OperationRejectedException("METHOD_NOT_SUPPORTED")
                        };
                        await Result(id, result);
                        if (method == "host.shutdown")
                        {
                            requestedShutdown = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        await Error(
                            id,
                            ex switch
                            {
                                OperationRejectedException e => e.Code,
                                OperationInstallException e => e.Code,
                                JsonException => "INVALID_REQUEST",
                                KeyNotFoundException => "INVALID_REQUEST",
                                InvalidOperationException => "INVALID_REQUEST",
                                _ => "HOST_REQUEST_FAILED"
                            }
                        );
                    }
                }
            }
            catch (Exception ex)when (ex is IOException or ObjectDisposedException)
            {
                connected = false;
            }
            finally
            {
                resources.Clear();
                activeConnection = null;
            }

            object Unsubscribe(string id)
            {
                subscriptions.Unsubscribe(id);
                return new
                {
                    unsubscribed = true
                };
            }
        }
        while (persistent && !requestedShutdown);
    }
    finally
    {
        resources.Clear();
        await runtime.ShutdownAsync(requestedShutdown);
    }

    async Task<JsonElement> Snapshot(string kind, string id)
    {
        object value = kind == "game"
            ? await launches.GetGameAsync(id)
            : kind == "page"
                ? await pages.GetAsync(id)
                : await GetOperation(id);
        return JsonSerializer.SerializeToElement(value, json);
    }

    async Task<SessionAttachment> SessionState(bool resumed)
    {
        var active = new List<ActiveGame>();
        foreach (var row in launches.ListSnapshots()
            .Cast<GameLaunchSnapshot>())
        {
            if (row.Result is { Detached: false } result && executor.BorrowLocalSession(result.GameProcessId)is { } game && !game.Completion.IsCompleted)
            {
                active.Add(
                    new(
                        executor.Details(result.GameProcessId),
                        row.Command.InstanceId,
                        row.Command.AccountId
                    )
                );
            }
        }

        return new(
            hostId,
            Environment.ProcessId,
            null,
            active.ToArray(),
            resumed
        );
    }

    async Task<object> CancelLogin(string id)
    {
        await accountService.CancelAsync(id);
        return new
        {
            cancelled = true
        };
    }

    object Logout(string id, bool remove)
    {
        accountService.Logout(id, remove);
        return new
        {
            completed = true
        };
    }

    async Task<object> GetOperation(string id)
    {
        var install = await installs.GetAsync(id);
        if (install is not null)
        {
            return install;
        }

        return (object? )await launches.GetAsync(id) ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
    }

    async Task<object> CancelOperation(string id)
    {
        if (await installs.GetAsync(id)is not null)
        {
            return await installs.CancelAsync(id);
        }

        return await launches.CancelAsync(id);
    }

    async Task<object> StopGame(string id)
    {
        await launches.StopGameAsync(id);
        return new
        {
            stopped = true
        };
    }

    async Task<object> OpenResource(JsonElement parameters)
    {
        var kind = parameters.GetProperty("kind")
            .GetString();
        if (kind == "page-operations")
        {
            return resources.OpenItems(
                (await pages.ListAsync()).Cast<object>()
                    .ToArray()
            );
        }

        if (kind == "artifact-export")
        {
            var row = await pages.GetAsync(
                parameters.GetProperty("operationId")
                    .GetString()!
            );
            if (row.State != "succeeded" || (row.Command.Method != "artifact.download" && row.Command.Method is not ("workspace.shell.diagnosticExport" or "workspace.instances.backup" or "workspace.instances.offlineScript" or "workspace.browser.export" or "workspace.packs.export" or "workspace.settings.export" or "workspace.catalog.icon" or "workspace.account.skinExport" or "workspace.instances.icon")))
            {
                throw new OperationRejectedException("EXPORT_NOT_READY");
            }

            var export = pageExecutor.OpenExport(
                pages.ExecutionId(row.OperationId)
            );
            return resources.OpenFile(export.Path, export.Size, export.Sha256);
        }

        if (kind == "operations")
        {
            var rows = (await installs.ListAsync()).Cast<object>()
                .Concat(
                    launches.ListSnapshots()
                )
                .ToArray();
            return resources.OpenItems(rows);
        }

        if (kind == "operation-log")
        {
            var id = parameters.GetProperty("operationId")
                .GetString()!;
            var snapshot = JsonSerializer.SerializeToElement(
                await GetOperation(id),
                json
            );
            // 最小结构化日志快照只有受控状态码，无原始异常、URL、令牌或宿主路径。receipt 不充当日志容器。
            return resources.OpenItems(
                journal.Read(id)
                    .Select(
                        entry => (object)new OperationLogEntry(entry.OperationId, entry.Sequence, entry.Level, entry.Code, entry.Truncated)
                    )
                    .ToArray()
            );
        }

        throw new OperationRejectedException("RESOURCE_KIND_UNSUPPORTED");
    }

    object CloseResource(string id)
    {
        resources.Close(id);
        return new
        {
            closed = true
        };
    }
}
catch (Exception ex)
{
    await Error(
        null,
        ex is IOException
            ? "HOST_BUSY_OR_IO_ERROR"
            : "HOST_INITIALIZATION_FAILED"
    );
    Environment.ExitCode = 1;
}

async Task Result(string? id, object? result) => await Send(
    new
    {
        protocolVersion = 1,
        id,
        result
    }
);
async Task Error(string? id, string code) => await Send(
    new
    {
        protocolVersion = 1,
        id,
        error = new
        {
            code
        }
    }
);
async Task Send(object envelope)
{
    if (!connected)
    {
        return;
    }

    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, json);
    if (bytes.Length > BoundedNdjson.MaximumBytes)
    {
        var original = JsonSerializer.SerializeToElement(envelope, json);
        var requestId = original.TryGetProperty("id", out var property)
            ? property.GetString()
            : null;
        bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                protocolVersion = 1,
                id = requestId,
                error = new
                {
                    code = "RESPONSE_TOO_LARGE"
                }
            },
            json
        );
    }

    try
    {
        if (activeConnection is not null)
        {
            await activeConnection.SendAsync(bytes);
        }
        else if (!pipeMode)
        {
            await output.WriteLineAsync(
                System.Text.Encoding.UTF8.GetString(bytes)
            );
        }
    }
    catch (IOException)
    {
        connected = false;
    }
}
