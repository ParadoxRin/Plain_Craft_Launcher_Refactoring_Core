using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Pcl.Core.Adapters.Protocol;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Host;
// 单个连接的有界可靠发送队列。慢消费者只断连接，不阻塞后台安装提交。
internal sealed class ProtocolConnection : IAsyncDisposable
{
    public Stream Input { get; }

    private readonly StreamWriter output;
    private readonly bool owns;
    public Action? DisconnectAction { get; set; }

    private readonly Channel<string> messages = Channel.CreateBounded<string>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        }
    );
    private readonly CancellationTokenSource stop = new();
    private readonly Task sender;
    public ProtocolConnection(Stream input, Stream destination, bool owns)
    {
        Input = input;
        this.owns = owns;
        output = new StreamWriter(destination, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        sender = SendLoopAsync();
    }

    public async Task SendAsync(byte[] bytes)
    {
        if (stop.IsCancellationRequested)
        {
            throw new IOException("CONNECTION_CLOSED");
        }

        try
        {
            await messages.Writer.WriteAsync(
                System.Text.Encoding.UTF8.GetString(bytes),
                stop.Token
            )
                .AsTask()
                .WaitAsync(
                    TimeSpan.FromSeconds(2)
                );
        }
        catch (Exception ex)when (ex is TimeoutException or OperationCanceledException or ChannelClosedException)
        {
            Abort();
            throw new IOException("SLOW_OR_DISCONNECTED_CONSUMER");
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var line in messages.Reader.ReadAllAsync(stop.Token))
            {
                await output.WriteLineAsync(
                    line.AsMemory(),
                    stop.Token
                );
            }
        }
        catch (Exception ex)when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            Abort();
        }
    }

    internal void Abort()
    {
        if (stop.IsCancellationRequested)
        {
            return;
        }

        stop.Cancel();
        messages.Writer.TryComplete();
        DisconnectAction?.Invoke();
        if (owns)
        {
            Input.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        messages.Writer.TryComplete();
        try
        {
            await sender.WaitAsync(
                TimeSpan.FromSeconds(2)
            );
        }
        catch (TimeoutException)
        {
            Abort();
            await sender;
        }

        // 对端已经断开时 Flush 可能再次报错；这属于本连接清理，不能让持有游戏的 Host 退出。
        try
        {
            output.Dispose();
        }
        catch (Exception ex)when (ex is IOException or ObjectDisposedException)
        {
        }

        if (owns)
        {
            Input.Dispose();
        }

        stop.Dispose();
    }
}
