using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：LinkRoom 汇集 Version、Name、Secret、GamePort、Peers，供该模块传递状态。
public sealed record LinkRoom(int Version, string Name, string Secret, int GamePort, string[] Peers)
{
    // [实现] Create 的实现：计算并返回 new( 1, "pclmac-" + Guid.NewGuid() .ToString("N")[..12], Convert.ToHexString( RandomNumberGener…。
    public static LinkRoom Create(int gamePort, string[] peers) => new(
        1,
        "pclmac-" + Guid.NewGuid()
            .ToString("N")[..12],
        Convert.ToHexString(
            RandomNumberGenerator.GetBytes(16)
        )
            .ToLowerInvariant(),
        gamePort,
        peers
    );
    // [实现] Validate 的实现：按 Version != 1 || GamePort is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(Nam… 分支处理；按 Peers.Length > 10 || Peers.Length == 0 分支处理；遍历 Peers 处理 peer。
    public void Validate()
    {
        if (Version != 1 || GamePort is < 1 or > 65535 || !System.Text.RegularExpressions.Regex.IsMatch(Name, "^[a-zA-Z0-9_-]{1,64}$") || !System.Text.RegularExpressions.Regex.IsMatch(Secret, "^[a-zA-Z0-9_-]{16,128}$"))
            throw new InvalidDataException("邀请码格式或房间端口无效。");
        if (Peers.Length > 10 || Peers.Length == 0)
            throw new InvalidDataException("需要一个可连接的节点地址。");
        foreach (var peer in Peers)
            if (!Uri.TryCreate(peer, UriKind.Absolute, out var uri) || uri.Scheme is not ("tcp" or "udp" or "ws" or "wss" or "quic") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || peer.Any(char.IsControl))
                throw new InvalidDataException("节点地址无效。");
    }

    // [实现] Encode 的实现：执行 Validate()；返回 "PCLMAC1:" + Convert.ToBase64String( Encoding.UTF8.GetBytes( JsonConvert.SerializeObject(this) …。
    public string Encode()
    {
        Validate();
        return "PCLMAC1:" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(this)
            )
        );
    }

    // [实现] Decode 的实现：按 text.Length > 8192 || !text.StartsWith("PCLMAC1:", StringComparison.Ordinal) 分支处理；执行受异常处理保护的操作。
    public static LinkRoom Decode(string text)
    {
        if (text.Length > 8192 || !text.StartsWith("PCLMAC1:", StringComparison.Ordinal))
            throw new InvalidDataException("请输入此 Mac 版生成的邀请码。");
        try
        {
            // [实现] 在异常保护范围内：准备 var room = JsonConvert.DeserializeObject<LinkRoom>( Encoding.UTF8.GetString( Convert.FromBase64…；执行 room.Validate()；返回 room。
            var room = JsonConvert.DeserializeObject<LinkRoom>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(
                        text[8..].Trim()
                    )
                )
            ) ?? throw new InvalidDataException();
            room.Validate();
            return room;
        }
        catch (Exception ex)when (ex is FormatException or Newtonsoft.Json.JsonException or NullReferenceException)
        {
            // [实现] 捕获 (Exception ex) 后：抛出 new InvalidDataException("邀请码内容无效。", ex)。
            throw new InvalidDataException("邀请码内容无效。", ex);
        }
    }
}

// 局域网联机：校验房间和节点参数，管理 EasyTier 子进程及本地端口转发，报告连接状态。
public sealed class EasyTierService(MinecraftService service)
{
    public const string Version = "2.6.4";
    private const string Digest = "4be1882d1aa36d31c1d6ba0596f2cf8a097e371f8da124212324b2e0f8df7e4b";
    // [实现] EnsureAsync 的实现：按 !OperatingSystem.IsMacOS() || service.Platform.Architecture != "aarch64" 分支处理；准备 var folder = Path.Combine(service.Paths.Runtime, "easytier-" + Version)；执行 Directory.CreateDirectory(folder)。
    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || service.Platform.Architecture != "aarch64")
            throw new PlatformNotSupportedException("当前联机模块包面向 Apple Silicon Mac。");
        var folder = Path.Combine(service.Paths.Runtime, "easytier-" + Version);
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "easytier.zip");
        await service.Downloads.DownloadAsync(
            new(
                new(
                    "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-macos-aarch64-v2.6.4.zip"
                ),
                archive,
                30174017,
                Digest
            ),
            ct
        );
        using var zip = ZipFile.OpenRead(archive);
        foreach (var name in new[]
        {
            "easytier-core",
            "easytier-cli"
        }

        )
        {
            // [实现] 对 new[] { "easytier-core", "easytier-cli" } 中的每一项：准备 var entry = zip.Entries.Single( x => x.Name == name )；按 entry.Length > 150_000_000 分支处理；准备 var target = ResourceInstaller.SafeTarget(folder, name)。
            var entry = zip.Entries.Single(
                x => x.Name == name
            );
            if (entry.Length > 150_000_000)
                throw new InvalidDataException("联机模块大小异常。");
            var target = ResourceInstaller.SafeTarget(folder, name);
            var temp = target + ".part";
            try
            {
                // [实现] 在异常保护范围内：在作用域内使用并释放 var output = File.Create(temp)；准备 var header = new byte[4096]；在作用域内使用并释放 var input = File.OpenRead(temp)。
                await using (var output = File.Create(temp))
                {
                    // [实现] 本段流程：准备 var input = entry.Open()；执行 await input.CopyToAsync(output, ct)。
                    using var input = entry.Open();
                    await input.CopyToAsync(output, ct);
                }

                var header = new byte[4096];
                using (var input = File.OpenRead(temp))
                    _ = input.Read(header);
                if (!MacNativeAudit.HasArm64(header))
                    throw new InvalidDataException("联机模块不是 ARM64 可执行文件。");
                File.SetUnixFileMode(
                    temp,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                );
                File.Move(temp, target, true);
            }
            finally
            {
                // [实现] 无论成功或失败，退出前：按 File.Exists(temp) 分支处理。
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        JsonFiles.Write(
            Path.Combine(folder, "provenance.json"),
            new
            {
                version = Version,
                source = "https://github.com/EasyTier/EasyTier/releases/tag/v2.6.4",
                sha256 = Digest,
                architecture = "aarch64",
                verifiedAt = DateTimeOffset.UtcNow
            }
        );
        return Path.Combine(folder, "easytier-core");
    }

    // [实现] FreePort 的实现：准备 var listener = new TcpListener(IPAddress.Loopback, 0)；执行 listener.Start()；准备 var port = ((IPEndPoint)listener.LocalEndpoint).Port。
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // [实现] Arguments 的实现：执行 room.Validate()；遍历 new[] { listenerPort, rpcPort, localPort } 处理 port；准备 var args = new List<string> { "--no-tun", "--network-name", room.Name, "--network-secret", room…。
    public static IReadOnlyList<string> Arguments(LinkRoom room, bool host, int listenerPort, int rpcPort, int localPort)
    {
        room.Validate();
        foreach (var port in new[]
        {
            listenerPort,
            rpcPort,
            localPort
        }

        )
            if (port is < 1 or > 65535)
                throw new InvalidDataException("联机端口无效。");
        var args = new List<string>
        {
            "--no-tun",
            "--network-name",
            room.Name,
            "--network-secret",
            room.Secret,
            "--listeners",
            $"tcp://0.0.0.0:{listenerPort}",
            "--rpc-portal",
            $"127.0.0.1:{rpcPort}",
            "--private-mode",
            "true",
            "--hostname",
            host
                ? "PCL-Mac-Host"
                : "PCL-Mac-Guest"
        };
        if (host)
            args.AddRange(
                [
                    "--ipv4",
                    "10.114.114.114",
                    "--tcp-whitelist",
                    room.GamePort.ToString(),
                    "--udp-whitelist",
                    room.GamePort.ToString()
                ]
            );
        else
            args.AddRange(
                [
                    "--dhcp",
                    "--tcp-whitelist",
                    "0",
                    "--udp-whitelist",
                    "0",
                    "--port-forward",
                    $"tcp://127.0.0.1:{localPort}/10.114.114.114:{room.GamePort}"
                ]
            );
        foreach (var peer in room.Peers)
            args.AddRange(
                [
                    "--peers",
                    peer
                ]
            );
        return args;
    }

    // [实现] StartAsync 的实现：准备 var binary = await EnsureAsync(ct)；准备 var rpc = FreePort()；准备 var start = new ProcessStartInfo(binary) { UseShellExecute = false, RedirectStandardOutput = tr…。
    public async Task<LinkProcess> StartAsync(
        LinkRoom room,
        bool host,
        int localPort = 25565,
        int listenPort = 11010,
        Action<string>? output = null,
        CancellationToken ct = default
    )
    {
        var binary = await EnsureAsync(ct);
        var rpc = FreePort();
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(binary)!
        };
        foreach (var arg in Arguments(room, host, listenPort, rpc, localPort))
            start.ArgumentList.Add(arg);
        var process = new Process
        {
            StartInfo = start
        };
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        // [实现] Read 的实现：按 e.Data is null 分支处理；准备 var line = e.Data.Replace(room.Secret, "<room-secret>")；执行 lines.Enqueue(line)。
        void Read(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
                return;
            var line = e.Data.Replace(room.Secret, "<room-secret>");
            lines.Enqueue(line);
            while (lines.Count > 200)
                lines.TryDequeue(out _);
            output?.Invoke(line);
        }

        process.OutputDataReceived += Read;
        process.ErrorDataReceived += Read;
        var started = false;
        try
        {
            // [实现] 在异常保护范围内：执行 process.Start()；执行 started = true；执行 process.BeginOutputReadLine()。
            process.Start();
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await Task.Delay(1200, ct);
            if (process.HasExited)
                throw new IOException(
                    "联机模块退出：" + string.Join("\n", lines)
                );
        }
        catch
        {
            // [实现] 捕获 空 后：按 started && !process.HasExited 分支处理；执行 process.Dispose()；重新抛出当前异常。
            if (started && !process.HasExited)
                process.Kill(true);
            process.Dispose();
            throw;
        }

        return new(
            process,
            rpc,
            Path.Combine(
                Path.GetDirectoryName(binary)!,
                "easytier-cli"
            ),
            service.Commands
        );
    }
}

// [实现] 数据约定：LinkProcess 汇集 Process、RpcPort、Cli，供该模块传递状态。
public sealed record LinkProcess(
    Process Process,
    int RpcPort,
    string Cli,
    Pcl.Core.Ports.Processes.IShellCommandRunner? Commands = null
) : IDisposable
{
    // 节点查询是短命令：通过注入的 Runner 排空双流，并在五秒超时后回收查询进程；不触碰联机守护进程。
    public async Task<string> PeersAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(5)
        );
        var result = await (Commands ?? new Pcl.Core.Adapters.Processes.SystemShellCommandRunner()).RunAsync(
            new(
                Cli,
                [
                    "-o",
                    "json",
                    "-p",
                    $"127.0.0.1:{RpcPort}",
                    "peer"
                ],
                Path.GetDirectoryName(
                    Path.GetFullPath(Cli)
                )!
            ),
            ct: timeout.Token
        )
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException("无法读取联机节点状态。");
        }

        return result.StandardOutput;
    }

    // [实现] Dispose 的实现：按 !Process.HasExited 分支处理；执行 Process.Dispose()。
    public void Dispose()
    {
        if (!Process.HasExited)
        {
            // [实现] 条件成立（!Process.HasExited）时：执行 Process.Kill(true)；执行 Process.WaitForExit(3000)。
            Process.Kill(true);
            Process.WaitForExit(3000);
        }

        Process.Dispose();
    }
}
