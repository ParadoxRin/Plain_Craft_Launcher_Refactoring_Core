using System.Net;
using System.Net.Sockets;
using System.Text;
using Pcl.Core;

// 局域网联机：校验房间和节点参数，管理 EasyTier 子进程及本地端口转发，报告连接状态。
internal static class LinkSmoke
{
    // [实现] RunAsync 的实现：准备 var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)；执行 timeout.CancelAfter( TimeSpan.FromSeconds(80) )；执行 ct = timeout.Token。
    public static async Task RunAsync(MinecraftService service, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(80)
        );
        ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var first = EasyTierService.FreePort();
        var second = EasyTierService.FreePort();
        var forwarded = EasyTierService.FreePort();
        var room = LinkRoom.Create(
            port,
            [
                $"tcp://127.0.0.1:{first}"
            ]
        );
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var engine = new EasyTierService(service);
        try
        {
            // [实现] 在异常保护范围内：准备 var echo = Task.Run( async () => { using var socket = await listener.AcceptTcpClientAsync(ct); …；准备 var host = await engine.StartAsync( room, true, localPort: forwarded, listenPort: first, output…；准备 var guest = await engine.StartAsync( room, false, localPort: forwarded, listenPort: second, out…。
            var echo = Task.Run(
                async () =>
                {
                    // [实现] 回调触发时：准备 var socket = await listener.AcceptTcpClientAsync(ct)；准备 var stream = socket.GetStream()；准备 var data = new byte[64]。
                    using var socket = await listener.AcceptTcpClientAsync(ct);
                    var stream = socket.GetStream();
                    var data = new byte[64];
                    var count = await stream.ReadAsync(data, ct);
                    await stream.WriteAsync(
                        data.AsMemory(0, count),
                        ct
                    );
                },
                ct
            );
            using var host = await engine.StartAsync(
                room,
                true,
                localPort: forwarded,
                listenPort: first,
                output: messages.Enqueue,
                ct: ct
            );
            using var guest = await engine.StartAsync(
                room,
                false,
                localPort: forwarded,
                listenPort: second,
                output: messages.Enqueue,
                ct: ct
            );
            var expected = Encoding.UTF8.GetBytes("pcl-mac-easytier-transport-test");
            var passed = false;
            for (int i = 0; i < 20 && !passed; i++)
            {
                // [实现] 每次迭代（i < 20 && !passed）：执行 await Task.Delay(1000, ct)；准备 var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct)；执行 attempt.CancelAfter( TimeSpan.FromSeconds(2) )。
                await Task.Delay(1000, ct);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(
                    TimeSpan.FromSeconds(2)
                );
                try
                {
                    // [实现] 在异常保护范围内：准备 var socket = new TcpClient()；执行 await socket.ConnectAsync(IPAddress.Loopback, forwarded, attempt.Token)；准备 var stream = socket.GetStream()。
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(IPAddress.Loopback, forwarded, attempt.Token);
                    var stream = socket.GetStream();
                    await stream.WriteAsync(expected, attempt.Token);
                    var received = new byte[expected.Length];
                    await stream.ReadExactlyAsync(received, attempt.Token);
                    passed = received.SequenceEqual(expected);
                }
                catch (Exception ex)when (ex is IOException or SocketException or OperationCanceledException)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 ct.ThrowIfCancellationRequested()。
                    ct.ThrowIfCancellationRequested();
                }
            }

            var peers = await guest.PeersAsync(ct);
            JsonFiles.Write(
                Path.Combine(service.Paths.Evidence, "easytier-smoke.json"),
                new
                {
                    status = passed
                        ? "transport_passed"
                        : "transport_failed",
                    version = EasyTierService.Version,
                    twoProcesses = true,
                    rootPermissions = false,
                    tcpEchoVerified = passed,
                    peers,
                    minecraftWorldPlay = "not_tested",
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            if (!passed)
                throw new IOException("EasyTier 双进程转发未通过，请检查日志。");
            await echo;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 listener.Stop()；执行 timeout.Cancel()；执行 File.WriteAllLines( Path.Combine(service.Paths.Evidence, "easytier-smoke.log"), messages )。
            listener.Stop();
            timeout.Cancel();
            File.WriteAllLines(
                Path.Combine(service.Paths.Evidence, "easytier-smoke.log"),
                messages
            );
        }
    }
}
