using System.Text.Json;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;

var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
var checks = new List<string>();
var hostExecutable = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE") ?? Path.GetFullPath(".tooling/dotnet/dotnet");
var hostAssembly = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")is null
    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
    : "";
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
    await using var client = new HostClient(hostExecutable, hostAssembly, root, reconnectable: true);
    var hello = await client.HelloAsync();
    var pid = client.HostPid;
    var id = hello.HostInstanceId;
    Check(
        pid > 0 && hello.Capabilities.Contains("session.reconnect.v1"),
        "persistent-host-handshake"
    );
    await client.DisconnectAsync();
    await client.ReconnectAsync();
    Check(
        client.HostPid == pid && client.HostId == id && client.Attachment!.LegacyGrant is null,
        "same-host-without-legacy-writer-after-reconnect"
    );
    var acceptance = await client.Installations.AcceptAsync(
        new(
            "reconnect-" + Guid.NewGuid()
                .ToString("N"),
            new("1.21.1", "vanilla", hello.InstallationTarget!)
        )
    );
    await using (var subscription = await client.SubscribeAsync("operation", acceptance.OperationId))
    {
        await client.DisconnectAsync();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(3)
        );
        long previous = -1;
        bool terminal = false;
        await foreach (var item in subscription.ReadAsync(timeout.Token))
        {
            Check(item.Sequence > previous, "event-sequence-increases");
            previous = item.Sequence;
            var state = item.Snapshot.GetProperty("state")
                .GetString();
            if (state is "succeeded" or "failed" or "cancelled" or "interrupted")
            {
                Check(state == "succeeded", "terminal-event-after-disconnect");
                terminal = true;
                break;
            }
        }

        Check(terminal && client.HostPid == pid, "reconnected-event-stream-keeps-backend");
    }

    // 新客户端不能在原 UI 存活时取得会话，即使传输已断开。
    await client.DisconnectAsync();
    await using (var other = new HostClient(hostExecutable, hostAssembly, root, reconnectable: true))
    {
        bool rejected = false;
        try
        {
            await other.HelloAsync();
        }
        catch (OperationRejectedException ex)when (ex.Code == "SESSION_IN_USE")
        {
            rejected = true;
        }

        Check(rejected, "second-client-cannot-take-live-owner");
    }

    await client.ReconnectAsync();
    await client.ShutdownAsync();
    await using (var session = new LauncherSession(
        new HostClient(hostExecutable, hostAssembly, root, reconnectable: true)
    ))
    {
        await session.InitializeAsync();
        File.WriteAllText(
            report + ".host-pid",
            session.Client.HostPid.ToString()
        );
        await session.Client.DisconnectAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session.IsAvailable && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Check(session.IsAvailable, "idle-page-automatically-reconnects");
        var accounts = await session.Client.Call<AccountSnapshot>(
            "account.snapshot",
            new { }
        );
        var account = accounts.Accounts.First(
            x => x.Kind == 0
        );
        var accepted = await session.Launches.AcceptAsync(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                account.Id,
                "1.21.1"
            )
        );
        var launched = await session.Launches.WaitAsync(accepted.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(3)
            );
        Check(launched.State == "succeeded", "game-launch-completes-through-subscription");
        var game = session.BorrowGame(launched.Result!.GameProcessId)!;
        try
        {
            var originalHost = session.Client.HostPid;
            await session.Client.DisconnectAsync();
            await session.Client.ReconnectAsync();
            Check(
                session.Client.HostPid == originalHost && session.Client.Attachment!.Games.Any(
                    x => x.Details.Pid == game.Pid
                ),
                "same-managed-game-restored-after-reconnect"
            );
            deadline = DateTime.UtcNow.AddMinutes(2);
            while (!game.HasExited && DateTime.UtcNow < deadline)
            {
                if (File.Exists(game.LogFile) && File.ReadAllText(game.LogFile)
                    .Contains("atlas"))
                {
                    break;
                }

                await Task.Delay(250);
            }

            if (game.Completion.IsFaulted)
            {
                await game.Completion;
            }

            Check(
                File.ReadAllText(game.LogFile)
                    .Contains("atlas"),
                "reconnected-game-renderer-initialized"
            );
        }
        finally
        {
            await game.StopAsync();
        }

        await game.Completion.WaitAsync(
            TimeSpan.FromSeconds(20)
        );
        Check(session.IsAvailable, "game-exit-event-keeps-client-available");
        await session.ShutdownAsync();
    }

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
                error = DiagnosticsService.Redact(
                    ex.ToString()
                )
            }
        )
    );
    Environment.ExitCode = 1;
}
