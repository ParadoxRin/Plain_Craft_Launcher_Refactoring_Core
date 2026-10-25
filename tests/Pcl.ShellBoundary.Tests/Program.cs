using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    checks.Add(name);
    Console.WriteLine(name);
}

LauncherSession Connect()
{
    var exe = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
    return new(
        new HostClient(
            exe ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
            exe is null
                ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                : "",
            root,
            reconnectable: true
        )
    );
}

// Seed a failed download receipt in an isolated directory; execution is the real Host downloader against loopback HTTP.
var bytes = new byte[512 * 1024];
Random.Shared.NextBytes(bytes);
var hash = Convert.ToHexString(
    SHA1.HashData(bytes)
)
    .ToLowerInvariant();
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
using var server = new HttpListener();
server.Prefixes.Add($"http://127.0.0.1:{port}/");
server.Start();
var file = Path.Combine(root, "download-retry.bin");
await File.WriteAllTextAsync(
    Path.Combine(root, "download-tasks.json"),
    JsonSerializer.Serialize(
        new[]
        {
            new
            {
                Id = "retry-fixture",
                Item = new
                {
                    Url = $"http://127.0.0.1:{port}/file",
                    Path = file,
                    Size = bytes.Length,
                    Sha1 = hash
                },
                State = "failed",
                Bytes = 0,
                Updated = DateTimeOffset.UtcNow
            }
        }
    )
);
var serve = Task.Run(
    async () =>
    {
        try
        {
            var c = await server.GetContextAsync();
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
);
await using var session = Connect();
try
{
    await session.InitializeAsync();
    var api = new WorkspaceApi(session.Workspaces);
    Check(
        !typeof(LauncherSession).Assembly.GetReferencedAssemblies()
            .Any(
                a => a.Name == "Pcl.Core"
            ),
        "client-assembly-has-no-core-reference"
    );
    Check(
        session.DesktopState.Paths.Root == root && session.DesktopState.Profiles.Any(),
        "host-supplies-profile-account-and-appearance-snapshot"
    );
    var forbidden = false;
    try
    {
        await session.Client.Call<JsonElement>(
            "desktop.acquire",
            new { }
        );
    }
    catch (OperationRejectedException)
    {
        forbidden = true;
    }

    Check(
        forbidden && session.Client.Attachment?.LegacyGrant is null,
        "legacy-writer-grant-removed"
    );
    var p = session.DesktopState.Profiles.First(
        x => x.GameVersion == "1.21.1" && x.Loader == "vanilla"
    );
    Check(
        (await api.CallAsync<ProfileAvailability>(
            "workspace.shell.profile",
            null,
            new
            {
                id = p.Id
            }
        )).Installed,
        "host-checks-installed-profile"
    );
    await api.CallAsync<bool>(
        "workspace.shell.selectProfile",
        null,
        new
        {
            id = p.Id
        }
    );
    Check(
        session.DesktopState.SelectedProfile == p.Id,
        "selected-profile-snapshot-refreshed-after-command"
    );
    await api.CallAsync<bool>(
        "workspace.shell.window",
        null,
        new
        {
            width = 1234,
            height = 777
        }
    );
    Check(
        JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(root, "launcher-window.json")
            )
        )
            .RootElement.GetProperty("width")
            .GetInt32() == 1234,
        "window-size-written-by-host"
    );
    var account = await api.CallAsync<LauncherAccount>(
        "workspace.account.addOffline",
        null,
        new
        {
            name = "PCL032_State"
        }
    );
    Check(
        session.DesktopState.Accounts.Accounts.Any(
            a => a.Id == account.Id
        ),
        "account-write-refreshes-display-snapshot"
    );
    await api.CallAsync<SettingsSnapshot>(
        "workspace.settings.save",
        null,
        new
        {
            changes = new
            {
                Appearance = session.DesktopState.Appearance with
                {
                    Title = "PCL032",
                    MusicAutoPlay = false
                },
                Network = new NetworkOptions
                {
                    ConcurrentFiles = 3
                }
            }
        }
    );
    Check(
        session.DesktopState.Appearance.Title == "PCL032",
        "settings-write-refreshes-appearance-snapshot"
    );
    var tasks = await session.Client.Call<DownloadSnapshot>(
        "downloads.snapshot",
        new { }
    );
    Check(
        tasks.Options.ConcurrentFiles == 3 && tasks.Rows.Any(
            r => r.Id == "retry-fixture"
        ),
        "download-and-network-snapshots-from-host"
    );
    await session.Client.Call<bool>(
        "downloads.pause",
        new
        {
            paused = true
        }
    );
    var retry = api.CallAsync<bool>(
        "workspace.shell.downloadRetry",
        null,
        new
        {
            id = "retry-fixture"
        }
    );
    await Task.Delay(600);
    Check(
        !retry.IsCompleted && (await session.Client.Call<DownloadSnapshot>(
            "downloads.snapshot",
            new { }
        )).IsPaused,
        "paused-real-download-remains-queryable"
    );
    var queuedInstall = session.Installation.Operations!.AcceptAsync(
        new(
            Guid.NewGuid()
                .ToString("N"),
            new("1.21.1", "vanilla", session.Target)
        )
    );
    await Task.Delay(150);
    Check(
        !queuedInstall.IsCompleted,
        "install-admission-waits-for-active-workspace-instead-of-host-busy-rejection"
    );
    await session.Client.Call<bool>(
        "downloads.pause",
        new
        {
            paused = false
        }
    );
    Check(
        await retry.WaitAsync(
            TimeSpan.FromSeconds(30)
        ) && SHA1.HashData(
            await File.ReadAllBytesAsync(file)
        )
            .SequenceEqual(
                SHA1.HashData(bytes)
            ),
        "resume-and-retry-real-download-with-hash-verification"
    );
    var acceptedInstall = await queuedInstall.WaitAsync(
        TimeSpan.FromSeconds(30)
    );
    var installResult = await session.Installation.Operations.WaitAsync(acceptedInstall.OperationId);
    Check(
        installResult.State == "succeeded",
        "queued-install-accepted-once-and-completes-after-workspace"
    );
    var log = Path.Combine(root, "evidence", "diagnostic-fixture.log");
    await File.WriteAllTextAsync(log, "java.lang.OutOfMemoryError\naccess_token=synthetic-secret\n");
    var diagnostic = await api.CallAsync<string>(
        "workspace.shell.diagnostic",
        null,
        new
        {
            path = log
        }
    );
    Check(
        diagnostic.Length > 0 && !diagnostic.Contains("synthetic-secret"),
        "host-diagnostic-analysis-redacts-secret"
    );
    var exported = Path.Combine(root, "exported-diagnostics.zip");
    await api.ExportAsync(
        "workspace.shell.diagnosticExport",
        null,
        new
        {
            path = log
        },
        exported
    );
    using (var zip = ZipFile.OpenRead(exported))
    {
        using var r = new StreamReader(
            zip.GetEntry("log-redacted.txt")!.Open()
        );
        Check(
            !(await r.ReadToEndAsync()).Contains("synthetic-secret"),
            "diagnostic-export-stream-is-redacted"
        );
    }

    var resource = Path.Combine(root, "drop-resource.zip");
    using (var zip = ZipFile.Open(resource, ZipArchiveMode.Create))
    {
        using (var w = new StreamWriter(
            zip.CreateEntry("pack.mcmeta")
                .Open()
        ))
            w.Write("{\"pack\":{\"pack_format\":34,\"description\":\"PCL032\"}}");
        zip.CreateEntry("assets/test/readme.txt");
    }

    Check(
        (await api.CallAsync<DropClassification>(
            "workspace.shell.classifyDrop",
            null,
            new
            {
                path = resource
            }
        )).Kind == "ResourcePack",
        "host-classifies-dropped-archive"
    );
    var imported = await api.CallAsync<ManagedResource>(
        "workspace.browser.import",
        p.Id,
        new
        {
            path = resource
        }
    );
    Check(
        imported.Name.Length > 0 && Directory.GetFiles(
            Path.Combine(p.GameDirectory, "resourcepacks"),
            "*.zip"
        )
            .Length > 0,
        "host-imports-real-resource-archive"
    );
    Check(
        !(await api.CallAsync<ReleaseNotice>("workspace.shell.releaseNotice", null)).Enabled,
        "disabled-release-notification-suppresses-query"
    );
    var before = session.DesktopState.Accounts.SelectedId;
    var oldSnapshot = session.DesktopState;
    var originalHost = session.Client.HostId;
    await session.Client.DisconnectAsync();
    var reconnectDeadline = DateTime.UtcNow.AddSeconds(15);
    while ((!session.Client.IsConnected || ReferenceEquals(session.DesktopState, oldSnapshot)) && DateTime.UtcNow < reconnectDeadline)
        await Task.Delay(100);
    Check(
        session.Client.IsConnected && !ReferenceEquals(session.DesktopState, oldSnapshot) && session.Client.HostId == originalHost,
        "automatic-reconnect-refreshes-public-snapshot-without-manual-refresh"
    );
    Check(
        session.DesktopState.Accounts.SelectedId == before && session.DesktopState.SelectedProfile == p.Id,
        "session-reconnect-restores-public-state-without-writer-grant"
    );
    await session.ShutdownAsync();
    await using (var reopened = Connect())
    {
        await reopened.InitializeAsync();
        Check(
            reopened.DesktopState.SelectedProfile == p.Id && reopened.DesktopState.Appearance.Title == "PCL032" && reopened.DesktopState.Accounts.Accounts.Any(
                a => a.Id == account.Id
            ),
            "host-restart-restores-profile-settings-and-account"
        );
        await reopened.ShutdownAsync();
    }

    await File.WriteAllTextAsync(
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
    await File.WriteAllTextAsync(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "failed",
                checks,
                error = ex.ToString()
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
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
finally
{
    server.Stop();
    await serve;
}
