using System.Text.Json;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Launch;

var mode = args[0];
var root = Path.GetFullPath(args[1]);
var report = Path.GetFullPath(args[2]);
var nativeHost = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
var dotnet = nativeHost ?? Path.GetFullPath(".tooling/dotnet/dotnet");
var assembly = nativeHost is null
    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
    : "";
var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception(name);
    }

    checks.Add(name);
}

try
{
    if (mode == "--request-identities")
    {
        var tracker = new Pcl.Core.Adapters.Protocol.RequestIdentityTracker();
        var prefix = Guid.NewGuid()
            .ToString("N");
        for (long i = 1; i <= 100000; i++)
        {
            if (!tracker.Accept(prefix + ":" + i))
            {
                throw new Exception("LONG_CONNECTION_ID_REJECTED");
            }
        }

        Check(
            !tracker.Accept(prefix + ":100000"),
            "replayed-ordered-id-rejected"
        );
        Check(
            !tracker.Accept(prefix + ":100002"),
            "skipped-ordered-id-rejected"
        );
        Check(
            !tracker.Accept(
                Guid.NewGuid()
                    .ToString("N") + ":100001"
            ),
            "changed-prefix-rejected"
        );
        Check(
            tracker.Accept(prefix + ":100001"),
            "ordered-id-continues-after-rejected-input"
        );
        Check(
            !tracker.Accept("legacy-id"),
            "mixed-id-mode-rejected"
        );
        var legacy = new Pcl.Core.Adapters.Protocol.RequestIdentityTracker();
        Check(
            legacy.Accept("old") && !legacy.Accept("old"),
            "legacy-id-dedup-preserved"
        );
        File.WriteAllText(
            report,
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    checks,
                    accepted = 100001
                }
            )
        );
        return;
    }

    if (mode == "--sessions")
    {
        var paths = new AppPaths(root);
        var secrets = new TestSecrets();
        var accounts = new AccountManager(paths, secrets);
        var id = "0123456789abcdef0123456789abcdef";
        var login = new MicrosoftLogin(
            new GameSession("FixtureUser", id, "fixture-access", "msa", false),
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            MicrosoftApplication.BuiltIn.RequireClientId()
        );
        var account = accounts.SaveSession(login);
        using var store = new Pcl.Core.Adapters.Operations.FileOperationStore(root, "account-operations-v1");
        var service = new Pcl.Core.Application.Accounts.HostAccountService(accounts, store);
        service.Restore();
        Check(
            service.Snapshot()
                .Accounts.Single()
                .HasCredential,
            "session-available-without-keychain"
        );
        accounts.ReloadIndex();
        using var auth = new MicrosoftAuthentication();
        Check(
            (await accounts.ResolveAsync(account.Id, auth)).AccessToken == "fixture-access",
            "session-survives-legacy-index-reload"
        );
        var appearanceWork = service.BeginAppearance(account.Id, "get", null, null, false);
        await service.CancelAsync(appearanceWork.OperationId);
        while (service.GetAppearance(appearanceWork.OperationId)
            .State == "running")
        {
            await Task.Delay(25);
        }

        Check(
            service.GetAppearance(appearanceWork.OperationId)
                .State == "cancelled",
            "host-appearance-explicit-cancel"
        );
        service.Logout(account.Id, false);
        Check(
            !service.Snapshot()
                .Accounts.Single()
                .HasCredential,
            "host-logout-clears-memory-session"
        );
        try
        {
            await accounts.ResolveAsync(account.Id, auth);
            throw new Exception("LOGOUT_STILL_RESOLVES");
        }
        catch (LoginRequiredException)
        {
            checks.Add("logged-out-session-rejected");
        }

        service.Logout(account.Id, true);
        Check(
            service.Snapshot()
                .Accounts.Length == 0,
            "host-remove-persists-index"
        );
        await Task.WhenAll(
            service.StopAsync(),
            service.StopAsync()
        );
        File.WriteAllText(
            report,
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    checks
                }
            )
        );
        return;
    }

    await using var client = new HostClient(dotnet, assembly, root);
    await client.HelloAsync();
    if (mode == "--accounts-restart")
    {
        var old = Directory.GetFiles(
            Path.Combine(root, "account-operations-v1"),
            "op-*.json"
        )
            .Select(File.ReadAllText)
            .Single();
        var oldId = JsonDocument.Parse(old)
            .RootElement.GetProperty("Snapshot")
            .GetProperty("OperationId")
            .GetString();
        var row = await client.Call<LoginSnapshot>(
            "account.login.begin",
            new
            {
                key = "one",
                remember = false
            }
        );
        Check(
            row.OperationId == oldId && row.State == "cancelled" && row.Prompt is null,
            "login-terminal-restored-no-reauthorization"
        );
    }
    else if (mode == "--accounts")
    {
        // 只对隔离目录发起匿名设备授权并取消，不要求用户登录，不访问真实账号的钥匙串。
        var grant = await client.Call<JsonElement>(
            "desktop.acquire",
            new { }
        );
        var token = grant.GetProperty("grant")
            .GetString();
        try
        {
            await client.Call<JsonElement>(
                "desktop.release",
                new
                {
                    grant = "invalid"
                }
            );
            throw new Exception("BAD_GRANT_ACCEPTED");
        }
        catch (OperationRejectedException e)when (e.Code == "INVALID_LEGACY_GRANT")
        {
            checks.Add("invalid-grant-rejected");
        }

        try
        {
            await client.Call<LoginSnapshot>(
                "account.login.begin",
                new
                {
                    key = "one",
                    remember = false
                }
            );
            throw new Exception("LEGACY_AUTH_ALLOWED");
        }
        catch (OperationRejectedException e)when (e.Code == "LEGACY_WRITER_ACTIVE")
        {
            checks.Add("login-blocked-during-legacy-work");
        }

        await client.Call<JsonElement>(
            "desktop.release",
            new
            {
                grant = token
            }
        );
        var login = await client.Call<LoginSnapshot>(
            "account.login.begin",
            new
            {
                key = "one",
                remember = false
            }
        );
        var replay = await client.Call<LoginSnapshot>(
            "account.login.begin",
            new
            {
                key = "one",
                remember = false
            }
        );
        Check(login.OperationId == replay.OperationId, "login-idempotent");
        try
        {
            await client.Call<LoginSnapshot>(
                "account.login.begin",
                new
                {
                    key = "one",
                    remember = true
                }
            );
            throw new Exception("KEY_CONFLICT_ACCEPTED");
        }
        catch (OperationRejectedException e)when (e.Code == "IDEMPOTENCY_CONFLICT")
        {
            checks.Add("login-key-conflict");
        }

        // 等到真实授权提示，确认协议没有 DeviceCode 或令牌，随后显式取消。
        var deadline = DateTime.UtcNow.AddSeconds(50);
        while (login.State == "queued" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
            login = await client.Call<LoginSnapshot>(
                "account.login.get",
                new
                {
                    operationId = login.OperationId
                }
            );
        }

        Check(login.State == "challenge", "live-device-challenge");
        var serialized = JsonSerializer.Serialize(login);
        Check(
            !serialized.Contains("DeviceCode") && !serialized.Contains("AccessToken") && !serialized.Contains("RefreshToken"),
            "no-secret-in-contract"
        );
        await client.Call<JsonElement>(
            "account.login.cancel",
            new
            {
                operationId = login.OperationId
            }
        );
        while (login.State is not ("cancelled" or "failed"))
        {
            await Task.Delay(100);
            login = await client.Call<LoginSnapshot>(
                "account.login.get",
                new
                {
                    operationId = login.OperationId
                }
            );
        }

        Check(login.State == "cancelled", "login-explicit-cancel");
        var receipt = Directory.GetFiles(
            Path.Combine(root, "account-operations-v1"),
            "op-*.json"
        )
            .Select(File.ReadAllText)
            .Single();
        Check(
            !receipt.Contains("UserCode") && !receipt.Contains("DeviceCode") && !receipt.Contains("VerificationUri"),
            "no-challenge-in-receipt"
        );
        // 工作 Task 已观察到取消后可再次借用旧窗口授权。
        await Task.Delay(100);
        await client.Call<JsonElement>(
            "desktop.acquire",
            new { }
        );
        checks.Add("legacy-access-restored-after-cancel");
    }
    else if (mode == "--microsoft")
    {
        // 只通过公开账户索引选择已记住的账户；凭据读取和刷新仅在 Host 内发生。
        var snapshot = await client.Call<AccountSnapshot>(
            "account.snapshot",
            new { }
        );
        var account = snapshot.Accounts.First(
            x => x.Kind == 1 && x.Remembered && x.HasCredential
        );
        var appearanceOperation = await client.Call<AppearanceSnapshot>(
            "account.appearance.begin",
            new
            {
                accountId = account.Id,
                action = "get"
            }
        );
        while (appearanceOperation.State == "running")
        {
            await Task.Delay(150);
            appearanceOperation = await client.Call<AppearanceSnapshot>(
                "account.appearance.get",
                new
                {
                    operationId = appearanceOperation.OperationId
                }
            );
        }

        Check(appearanceOperation.Profile?.Id.Length == 32, "host-microsoft-profile-and-skins-read");
        // Exercise the migrated skin page path with the same Host-owned Microsoft session; no profile mutation.
        var pages = new HostPageOperations(client);
        var viewAccepted = await pages.AcceptAsync(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                "workspace.account.appearance",
                PayloadJson: JsonSerializer.Serialize(
                    new
                    {
                        accountId = account.Id,
                        action = "get"
                    }
                )
            )
        );
        var viewRow = await pages.WaitAsync(viewAccepted.OperationId);
        Check(viewRow.State == "succeeded", "host-workspace-microsoft-appearance-completed");
        var view = viewRow.Result!.Value.Deserialize<Pcl.Core.Contracts.Workspaces.SkinAppearance>(
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        )!;
        Check(
            view.Profile.Id == account.Uuid && (view.Profile.Skins.Length == 0 || view.Png is { Length: > 100 }),
            "host-workspace-microsoft-public-profile-and-normalized-preview"
        );
        var command = new GameLaunchCommand(
            Guid.NewGuid()
                .ToString("N"),
            account.Id,
            "1.21.1"
        );
        var accepted = await client.Launches.AcceptAsync(command);
        var row = await client.Launches.WaitAsync(accepted.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(3)
            );
        Check(row.State == "succeeded", "host-microsoft-refresh-entitlement-start");
        var game = row.Result!;
        var details = await client.Call<GameDetails>(
            "game.details",
            new
            {
                gameProcessId = game.GameProcessId
            }
        );
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            bool rendered = false;
            while (DateTime.UtcNow < deadline)
            {
                var text = File.Exists(details.LogFile)
                    ? File.ReadAllText(details.LogFile)
                    : "";
                if (text.Contains("Created:") && text.Contains("atlas"))
                {
                    rendered = true;
                    break;
                }

                if ((await client.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                {
                    break;
                }

                await Task.Delay(250);
            }

            Check(rendered, "host-microsoft-renderer-initialized");
            var replay = await client.Launches.AcceptAsync(command);
            Check(replay.Replayed && replay.OperationId == row.OperationId, "host-microsoft-launch-replay");
        }
        finally
        {
            await client.Launches.StopGameAsync(game.GameProcessId);
        }

        while ((await client.Launches.GetGameAsync(game.GameProcessId)).State != "exited")
        {
            await Task.Delay(100);
        }

        await Task.Delay(200);
        await client.Call<JsonElement>(
            "desktop.snapshot",
            new { }
        );
        checks.Add("host-microsoft-contract-snapshot-after-game");
    }

    await client.ShutdownAsync();
    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                checks
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}
catch (Exception ex)
{
    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "failed",
                checks,
                error = DiagnosticsService.Redact(ex.Message)
            }
        )
    );
    Environment.ExitCode = 1;
}

// 本测试不调用系统钥匙串；测试凭据只在内存里，终态摘要不输出其值。
sealed class TestSecrets : IAccountSecrets
{
    private readonly Dictionary<string, string> values = new();
    public string? Get(string id) => values.GetValueOrDefault(id);
    public void Set(string id, string value) => values[id] = value;
    public void Delete(string id) => values.Remove(id);
}
