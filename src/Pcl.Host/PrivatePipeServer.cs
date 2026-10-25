using System.IO.Pipes;
using System.Text.Json;
using Pcl.Core.Adapters.Protocol;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Host;
// OS 当前用户身份校验 + 0700 私有目录 + 0600 socket；不监听 TCP，不存储游戏或微软凭据。
internal sealed class PrivatePipeServer : IDisposable
{
    private readonly NamedPipeServerStream server;
    public PrivatePipeServer(string root)
    {
        var path = Pcl.Protocol.PrivatePipePath.ForRoot(root);
        Pcl.Protocol.PrivatePipePath.SecureParent(path);
        if (!OperatingSystem.IsWindows())
        {
            // 只能在已经获得数据目录 OS 根锁后创建；残留 socket 不是存活 Host 的证据。
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException("PIPE_PATH_UNSAFE");
            }

            File.Delete(path);
        }

        server = new(
            path,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            65536,
            65536
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public async Task<ProtocolConnection> AcceptAsync(Func<ClientIdentity, Task<SessionAttachment>> authorize)
    {
        while (true)
        {
            // Broken 管道的 IsConnected 已为 false，但仍须 Disconnect 才能再次等待连接。
            // 尚未连接的初始状态会拒绝 Disconnect，允许它继续首次等待。
            try
            {
                server.Disconnect();
            }
            catch (InvalidOperationException)
            {
            }

            await server.WaitForConnectionAsync();
            var connection = new ProtocolConnection(server, server, false)
            {
                DisconnectAction = Disconnect
            };
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5)
                );
                await using var frames = BoundedNdjson.ReadAsync(server, timeout.Token)
                    .GetAsyncEnumerator();
                if (!await frames.MoveNextAsync() || frames.Current.Error is not null)
                {
                    throw new IOException("ATTACH_FRAME_INVALID");
                }

                using var doc = JsonDocument.Parse(frames.Current.Data!);
                var value = doc.RootElement;
                if (value.GetProperty("protocolVersion")
                    .GetInt32() != 1 || value.GetProperty("method")
                    .GetString() != "session.attach")
                {
                    throw new OperationRejectedException("ATTACH_REQUIRED");
                }

                var identity = value.GetProperty("params")
                    .Deserialize<ClientIdentity>(
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    )!;
                var result = await authorize(identity);
                await connection.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            protocolVersion = 1,
                            attached = true,
                            result
                        },
                        new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                        }
                    )
                );
                return connection;
            }
            catch (Exception ex)when (ex is OperationRejectedException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                try
                {
                    await connection.SendAsync(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                protocolVersion = 1,
                                error = new
                                {
                                    code = ex is OperationRejectedException rejection
                                        ? rejection.Code
                                        : "ATTACH_REJECTED"
                                }
                            }
                        )
                    );
                }
                catch (IOException)
                {
                }

                await connection.DisposeAsync();
                if (server.IsConnected)
                {
                    server.Disconnect();
                }
            }
        }
    }

    public void Disconnect()
    {
        try
        {
            if (server.IsConnected)
            {
                server.Disconnect();
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => server.Dispose();
}
