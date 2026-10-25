using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

internal static class CompatibilityFlow
{
    internal static async Task Run(string fixture, string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        var wire = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        using var original = JsonDocument.Parse(
            File.ReadAllText(fixture)
        );
        var snapshot = original.RootElement.GetProperty("Snapshot")
            .Deserialize<PageSnapshot>(wire)!;
        Directory.CreateDirectory(
            Path.Combine(root, "page-operations-v1")
        );
        File.Copy(
            fixture,
            Path.Combine(root, "page-operations-v1", snapshot.OperationId + ".json")
        );
        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var session = new LauncherSession(
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
            await session.Client.HelloAsync();
            var pages = new HostPageOperations(session.Client);
            var replay = await pages.AcceptAsync(snapshot.Command);
            Check(
                replay.Replayed && replay.OperationId == snapshot.OperationId,
                "actual-0.27-receipt-replays-after-upgrade"
            );
            bool conflict = false;
            try
            {
                await pages.AcceptAsync(
                    snapshot.Command with
                    {
                        GameVersion = "1.20.1"
                    }
                );
            }
            catch (OperationRejectedException ex)
            {
                conflict = ex.Code == "IDEMPOTENCY_CONFLICT";
            }

            Check(conflict, "old-receipt-still-rejects-changed-input");
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            async Task Mime(string method, string expected, object? args = null)
            {
                var row = await session.Workspaces.ExecuteAsync(
                    method,
                    "1.21.1",
                    JsonSerializer.Serialize(
                        args ?? new { }
                    )
                );
                var resource = await session.Client.OpenResourceAsync("artifact-export", row.OperationId);
                Check(resource.Mime == expected, "controlled-export-mime-" + expected + "-" + method);
                await session.Client.CloseResourceAsync(resource.ResourceId);
            }

            await Mime("workspace.instances.backup", "application/zip");
            var icon = Path.Combine(root, "compat-icon.png");
            File.WriteAllBytes(
                icon,
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
            );
            await api.CallAsync<bool>(
                "workspace.instances.saveIcon",
                "1.21.1",
                new
                {
                    path = icon
                }
            );
            await Mime("workspace.instances.icon", "image/png");
            await Mime(
                "workspace.packs.export",
                "application/zip",
                new
                {
                    options = new PackExportOptions("Compatibility export")
                }
            );
            await session.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        fixture = Path.GetFileName(fixture)
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
                        error = ex.Message
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
