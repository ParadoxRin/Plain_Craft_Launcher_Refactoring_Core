using System.Diagnostics;
using System.Text.Json;
using Pcl.Host;
using Pcl.Core.Contracts.Operations;

var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception(name);
    }

    checks.Add(name);
}

var broker = new OperationEventBroker();
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int disconnected = 0, delivered = 0;
await using (var subscriptions = new OperationSubscriptions(
    "host-test",
    broker,
    (_, _) => Task.FromException<JsonElement>(
        new OperationRejectedException("NOT_FOUND")
    ),
    async _ =>
    {
        await release.Task;
        Interlocked.Increment(ref delivered);
    },
    () => Interlocked.Increment(ref disconnected)
))
{
    subscriptions.Subscribe(
        "sub-" + Guid.NewGuid()
            .ToString("N"),
        "operation",
        "op-test",
        -1
    );
    var timer = Stopwatch.StartNew();
    for (int i = 1; i <= 1000; i++)
    {
        broker.Changed(
            new(
                "op-test",
                i,
                "running",
                "stage-" + i,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        sequence = i,
                        state = "running",
                        stage = "stage-" + i
                    }
                )
            )
        );
    }

    Check(
        timer.Elapsed < TimeSpan.FromSeconds(2),
        "slow-consumer-does-not-block-operation-observer"
    );
    Check(disconnected > 0, "critical-event-overflow-forces-resynchronization");
    release.SetResult();
    await Task.Delay(150);
    Check(delivered <= 129, "critical-event-queue-is-bounded");
}

var aborted = false;
await using (var connection = new ProtocolConnection(
    Stream.Null,
    new BlockedStream(),
    false
)
{
    DisconnectAction = () => aborted = true
}

)
{
    bool rejected = false;
    try
    {
        for (int i = 0; i < 150; i++)
        {
            await connection.SendAsync(new byte[4096]);
        }
    }
    catch (IOException)
    {
        rejected = true;
    }

    Check(rejected && aborted, "blocked-transport-aborts-instead-of-growing-unbounded");
}

File.WriteAllText(
    args[0],
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
// 模拟永不消费的客户端；取消仍可唤醒写入，验证发送任务由连接释放。
sealed class BlockedStream : Stream
{
    public override bool CanRead => false;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => await Task.Delay(Timeout.Infinite, ct);
}
