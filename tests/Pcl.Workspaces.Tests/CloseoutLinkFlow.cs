using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

internal static class CloseoutLinkFlow
{
    // Focused real two-node acceptance. No game launch, Microsoft login or public relay is used.
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
        try
        {
            await host.InitializeAsync();
            await guest.InitializeAsync();
            var api = new WorkspaceApi(host.Workspaces);
            var other = new WorkspaceApi(guest.Workspaces);
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
                    var bytes = Encoding.UTF8.GetBytes("PCL-Closeout-forward-proof");
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
            Check(
                (await api.CallAsync<LinkSnapshot>(
                    "workspace.link.stop",
                    null,
                    new
                    {
                        id = h.Id
                    }
                )).State == "stopped",
                "host-stops-owned-room"
            );
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
