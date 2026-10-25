using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

internal static class MainLinkFlow
{
    // Two real Host processes and EasyTier nodes. The destination is a disposable loopback echo server, never a public relay.
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var p = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return p;
        }

        LauncherSession Connect(string folder)
        {
            var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
            return new(
                new HostClient(
                    native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                    native is null
                        ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                        : "",
                    folder,
                    reconnectable: true
                )
            );
        }

        await using var host = Connect(root);
        await using var guest = Connect(root + "-guest");
        using var stopEcho = new CancellationTokenSource();
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var gamePort = ((IPEndPoint)echo.LocalEndpoint).Port;
        async Task Echo()
        {
            try
            {
                while (!stopEcho.IsCancellationRequested)
                {
                    using var c = await echo.AcceptTcpClientAsync(stopEcho.Token);
                    var stream = c.GetStream();
                    var buffer = new byte[4096];
                    var n = await stream.ReadAsync(buffer, stopEcho.Token);
                    await stream.WriteAsync(
                        buffer.AsMemory(0, n),
                        stopEcho.Token
                    );
                }
            }
            catch (OperationCanceledException)when (stopEcho.IsCancellationRequested)
            {
            }
        }

        var echoTask = Echo();
        string? gameId = null;
        try
        {
            await host.InitializeAsync();
            await guest.InitializeAsync();
            var api = new WorkspaceApi(host.Workspaces);
            var other = new WorkspaceApi(guest.Workspaces);
            var versions = await api.CallAsync<MinecraftRelease[]>("workspace.desktop.versions", null);
            Check(
                versions.Any(
                    x => x.Id == "1.21.1"
                ) && versions.Any(
                    x => x.Type == "snapshot"
                ),
                "host-official-release-and-snapshot-catalog"
            );
            string? fabricVersion = null;
            foreach (var loader in new[]
            {
                "fabric",
                "quilt",
                "forge",
                "neoforge"
            }

            )
            {
                var rows = await api.CallAsync<string[]>(
                    "workspace.desktop.loaderVersions",
                    null,
                    new
                    {
                        gameVersion = "1.21.1",
                        loader
                    }
                );
                Check(rows.Length > 0, "host-live-loader-versions-" + loader);
                if (loader == "fabric")
                {
                    fabricVersion = rows[0];
                }
            }

            var rejected = false;
            try
            {
                await api.CallAsync<GameProfile>(
                    "workspace.desktop.loaderInstall",
                    null,
                    new
                    {
                        gameVersion = "1.21.1",
                        loader = "fabric",
                        version = "invalid-version"
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "LOADER_VERSION_INVALID")
            {
                rejected = true;
            }

            Check(rejected, "host-rejects-unknown-loader-version-before-install");
            var profile = await api.CallAsync<GameProfile>(
                "workspace.desktop.loaderInstall",
                null,
                new
                {
                    gameVersion = "1.21.1",
                    loader = "fabric",
                    version = fabricVersion
                }
            );
            Check(
                profile.Loader == "fabric" && File.Exists(profile.JsonPath),
                "host-installs-selected-fabric-version"
            );
            var mac = await api.CallAsync<GameProfile>(
                "workspace.desktop.macSetup",
                null,
                new
                {
                    shaders = false
                }
            );
            Check(
                mac.Loader == "fabric" && Directory.GetFiles(
                    Path.Combine(mac.GameDirectory, "mods"),
                    "*.jar"
                )
                    .Length > 0,
                "host-creates-real-mac-basic-configuration"
            );
            var bad = Path.Combine(root, "invalid-optifabric.jar");
            await File.WriteAllTextAsync(bad, "invalid");
            var invalid = false;
            try
            {
                await api.CallAsync<bool>(
                    "workspace.desktop.optiFabricValidate",
                    null,
                    new
                    {
                        path = bad
                    }
                );
            }
            catch (OperationRejectedException)
            {
                invalid = true;
            }

            Check(invalid, "host-rejects-invalid-optifabric-input");
            var optiFile = Path.GetFullPath("artifacts/evidence-0.8-compat/optifabric/optifabric-1.14.3.jar");
            Check(
                await api.CallAsync<bool>(
                    "workspace.desktop.optiFabricValidate",
                    null,
                    new
                    {
                        path = optiFile
                    }
                ),
                "host-validates-existing-reviewed-optifabric-artifact"
            );
            // Exercise the actual legacy installer through the new Host boundary, without a CurseForge key.
            var optiProfile = await api.CallAsync<GameProfile>("workspace.desktop.optiFabricInstall", null, new { path = optiFile });
            Check(File.Exists(Path.Combine(optiProfile.GameDirectory, "pcl-optifabric.json")) && Directory.GetFiles(Path.Combine(optiProfile.GameDirectory, "mods"), "*.jar").Length >= 2, "host-installs-real-optifabric-compatibility-instance");
            using (var loginCancel = new CancellationTokenSource(
                TimeSpan.FromSeconds(40)
            ))
            {
                var prompted = false;
                var cancelled = false;
                try
                {
                    await ((Pcl.Core.Contracts.Accounts.IAccountLogin)host).LoginAsync(
                        false,
                        prompt =>
                        {
                            prompted = prompt.UserCode.Length > 0;
                            loginCancel.Cancel();
                        },
                        loginCancel.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Check(
                    prompted && cancelled && host.IsAvailable,
                    "contract-microsoft-device-challenge-cancel-restores-client"
                );
            }

            var invalidPort = false;
            try
            {
                await api.CallAsync<LinkSnapshot>(
                    "workspace.link.start",
                    null,
                    new
                    {
                        host = true,
                        gamePort = 0,
                        peers = new[]
                        {
                            "tcp://127.0.0.1:11010"
                        }
                    }
                );
            }
            catch (OperationRejectedException)
            {
                invalidPort = true;
            }

            Check(invalidPort, "invalid-room-rejected-before-process-start");
            var listenerPort = FreePort();
            var h = await api.CallAsync<LinkSnapshot>(
                "workspace.link.start",
                null,
                new
                {
                    host = true,
                    gamePort,
                    listenPort = listenerPort,
                    peers = new[]
                    {
                        "tcp://127.0.0.1:" + listenerPort
                    },
                    localPort = FreePort()
                }
            );
            var invite = await api.LinkInvitationAsync(h.Id);
            Check(
                invite.StartsWith("PCLMAC1:"),
                "host-starts-real-easytier-and-returns-private-invitation"
            );
            var duplicate = false;
            try
            {
                await api.CallAsync<LinkSnapshot>(
                    "workspace.link.start",
                    null,
                    new
                    {
                        host = true,
                        gamePort,
                        peers = new[]
                        {
                            "tcp://127.0.0.1:" + listenerPort
                        }
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "LINK_ALREADY_RUNNING")
            {
                duplicate = true;
            }

            Check(duplicate, "second-room-rejected-without-leaking-process");
            var secretHandle = await other.SecretAsync(invite);
            var forwardedPort = FreePort();
            var g = await other.CallAsync<LinkSnapshot>(
                "workspace.link.start",
                null,
                new
                {
                    host = false,
                    localPort = forwardedPort,
                    listenPort = FreePort(),
                    secretHandle
                }
            );
            bool linked = false;
            for (int i = 0; i < 30; i++)
            {
                var hs = await api.CallAsync<LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id = h.Id
                    }
                );
                var gs = await other.CallAsync<LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id = g.Id
                    }
                );
                if (hs.PeerCount > 0 && gs.PeerCount > 0)
                {
                    linked = true;
                    break;
                }

                await Task.Delay(500);
            }

            Check(linked, "two-real-local-easytier-nodes-connected");
            bool forwarded = false;
            Exception? lastForwardFailure = null;
            for (int attempt = 0; attempt < 20 && !forwarded; attempt++)
            {
                try
                {
                    using var socket = new TcpClient();
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(3)
                    );
                    await socket.ConnectAsync(IPAddress.Loopback, forwardedPort, timeout.Token);
                    var bytes = Encoding.UTF8.GetBytes("PCL031-forward-proof");
                    var stream = socket.GetStream();
                    await stream.WriteAsync(bytes, timeout.Token);
                    var returned = new byte[bytes.Length];
                    await stream.ReadExactlyAsync(returned, timeout.Token);
                    forwarded = returned.SequenceEqual(bytes);
                }
                catch (Exception ex)when (ex is IOException or SocketException or OperationCanceledException)
                {
                    lastForwardFailure = ex;
                }

                if (!forwarded)
                {
                    await Task.Delay(500);
                }
            }

            if (!forwarded)
            {
                throw new IOException("Overlay payload never became ready", lastForwardFailure);
            }

            Check(forwarded, "real-tcp-payload-crosses-overlay-forward-and-returns");
            var raw = Encoding.UTF8.GetString(
                Convert.FromBase64String(invite[8..])
            );
            using var room = JsonDocument.Parse(raw);
            var secret = room.RootElement.GetProperty("Secret")
                .GetString()!;
            Check(
                !Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                    .Any(
                        p => File.ReadAllText(p)
                            .Contains(secret)
                    ) && !Directory.EnumerateFiles(root + "-guest", "*.json", SearchOption.AllDirectories)
                    .Any(
                        p => File.ReadAllText(p)
                            .Contains(secret)
                    ),
                "room-credentials-not-written-to-receipts-or-settings"
            );
            await host.Client.DisconnectAsync();
            await host.Client.ReconnectAsync();
            Check(
                (await api.CallAsync<LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id = h.Id
                    }
                )).State == "running",
                "client-reconnect-retains-host-owned-room"
            );
            var account = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL031_Link"
                }
            );
            var accepted = await host.Launches.AcceptAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    account.Id,
                    "1.21.1"
                )
            );
            var launch = await host.Launches.WaitAsync(accepted.OperationId);
            Check(launch.State == "succeeded", "offline-game-launch-with-room-running");
            gameId = launch.Result!.GameProcessId;
            Check(
                (await api.CallAsync<LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id = h.Id
                    }
                )).State == "running" && host.Client.Attachment?.LegacyGrant is null,
                "multiplayer-status-during-game-has-no-legacy-writer-grant"
            );
            var blocked = false;
            try
            {
                await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            }
            catch (OperationRejectedException e)when (e.Code == "HOST_WRITER_ACTIVE")
            {
                blocked = true;
            }

            Check(blocked, "multiplayer-exception-does-not-open-unrelated-game-time-writes");
            Check(
                (await api.CallAsync<LinkSnapshot>(
                    "workspace.link.stop",
                    null,
                    new
                    {
                        id = h.Id
                    }
                )).State == "stopped",
                "stop-room-while-minecraft-is-running"
            );
            Check(
                (await host.Launches.GetGameAsync(gameId)).State != "exited",
                "stopping-room-does-not-stop-minecraft"
            );
            await host.Launches.StopGameAsync(gameId);
            while ((await host.Launches.GetGameAsync(gameId)).State != "exited")
            {
                await Task.Delay(100);
            }

            gameId = null;
            await other.CallAsync<LinkSnapshot>(
                "workspace.link.stop",
                null,
                new
                {
                    id = g.Id
                }
            );
            var stoppedInvitation = false;
            try
            {
                await api.LinkInvitationAsync(h.Id);
            }
            catch (OperationRejectedException)
            {
                stoppedInvitation = true;
            }

            Check(stoppedInvitation, "stopped-room-invitation-is-unavailable");
            var closingPort = FreePort();
            await api.CallAsync<LinkSnapshot>(
                "workspace.link.start",
                null,
                new
                {
                    host = true,
                    gamePort,
                    listenPort = closingPort,
                    peers = new[]
                    {
                        "tcp://127.0.0.1:" + closingPort
                    }
                }
            );
            await guest.ShutdownAsync();
            await host.ShutdownAsync();
            bool stillListening = false;
            using (var probe = new TcpClient())
            {
                try
                {
                    await probe.ConnectAsync(IPAddress.Loopback, closingPort);
                    stillListening = true;
                }
                catch (SocketException)
                {
                }
            }

            Check(!stillListening, "host-shutdown-stops-owned-easytier-listener");
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "passed",
                        Checks = checks,
                        Limitations = "Loopback overlay test; not a remote Internet NAT or two-player world acceptance."
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
            if (gameId is not null)
            {
                try
                {
                    await host.Launches.StopGameAsync(gameId);
                }
                catch
                {
                }
            }

            try
            {
                await host.ShutdownAsync();
            }
            catch
            {
            }

            try
            {
                await guest.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            stopEcho.Cancel();
            echo.Stop();
            await echoTask;
        }
    }
}
