using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

internal static class ShutdownFlow
{
    // Keep the stopped client alive while another session attaches: disposal must not be hiding a monitor resurrection.
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        var checks = new List<string>();
        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        LauncherSession Create() => new(
            new HostClient(
                native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                native is null
                    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                    : "",
                root,
                reconnectable: true
            )
        );
        try
        {
            for (int i = 0; i < 6; i++)
            {
                await using var old = Create();
                await old.InitializeAsync();
                var account = await new WorkspaceApi(old.Workspaces).CallAsync<LauncherAccount>(
                    "workspace.account.addOffline",
                    null,
                    new
                    {
                        name = "Restart" + i
                    }
                );
                var hostId = old.Client.HostId;
                await old.ShutdownAsync();
                // One monitor interval elapses with the old object still live and no disposal-based cancellation.
                await Task.Delay(300);
                if (old.Client.IsConnected)
                {
                    throw new Exception("stopped-session-reconnected");
                }

                await using var next = Create();
                await next.InitializeAsync();
                var index = await new WorkspaceApi(next.Workspaces).CallAsync<AccountPageSnapshot>("workspace.account.list", null);
                if (next.Client.HostId == hostId || !index.Accounts.Any(
                    x => x.Id == account.Id
                ))
                {
                    throw new Exception("restart-did-not-restore-account");
                }

                await next.ShutdownAsync();
                checks.Add("shutdown-with-live-old-client-and-new-host-" + i);
            }

            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "passed",
                        Checks = checks
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
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "failed",
                        Checks = checks,
                        Error = ex.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
            throw;
        }
    }
}
