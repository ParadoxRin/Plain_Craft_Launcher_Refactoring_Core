using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pcl.Core.Ports.Processes;

namespace Pcl.Core.Adapters.Processes;
/// <summary>拥有长游戏进程、日志泵和退出观察；逻辑句柄绑定原 Process，不通过裸 PID 重新接管。</summary>
public sealed class SystemGameProcessHost : IGameProcessHost
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new();
    private bool disposed;
    public Task<GameProcessHandle> StartAsync(ProcessSpec spec, GameProcessOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var file = Path.GetFullPath(options.LogFile);
            Directory.CreateDirectory(
                Path.GetDirectoryName(file)!
            );
            Process process;
            StreamWriter? writer = null;
            if (options.Detached)
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("独立游戏文件重定向当前使用 Unix Adapter。");
                }

                // 固定脚本不拼接任何用户参数；保留原独立运行时直接追加文件的行为。
                var environment = new Dictionary<string, string>(
                    spec.Environment ?? new Dictionary<string, string>()
                )
                {
                    ["PCL_DETACHED_LOG"] = file
                };
                process = ProcessExecution.Start(
                    new ProcessSpec(
                        "/bin/sh",
                        new[]
                        {
                            "-c",
                            "exec \"$@\" >> \"$PCL_DETACHED_LOG\" 2>&1",
                            "pcl-game",
                            spec.Executable
                        }.Concat(spec.Arguments)
                            .ToArray(),
                        spec.WorkingDirectory
                    )
                    {
                        Environment = environment
                    },
                    false
                );
            }
            else
            {
                writer = new StreamWriter(file)
                {
                    AutoFlush = true
                };
                try
                {
                    process = ProcessExecution.Start(spec, true);
                }
                catch
                {
                    writer.Dispose();
                    throw;
                }
            }

            var handle = new GameProcessHandle(
                Guid.NewGuid()
                    .ToString("N"),
                process.Id
            );
            var entry = new Entry(handle, process, file, options.Detached, writer);
            entries.Add(handle.Id, entry);
            entry.Completion = MonitorAsync(entry, spec.SensitiveValues);
            return Task.FromResult(handle);
        }
    }

    public bool IsRunning(GameProcessHandle handle)
    {
        var entry = Find(handle);
        return !entry.Completion.IsCompleted && !entry.Process.HasExited;
    }

    public Task<int> WaitForExitAsync(GameProcessHandle handle, CancellationToken ct = default) => Find(handle)
        .Completion.WaitAsync(ct);
    // force=false 目前只向拥有的 Unix 游戏进程发 SIGTERM；force=true 维持原整树结束行为。
    public async Task TerminateAsync(GameProcessHandle handle, bool force = true, CancellationToken ct = default)
    {
        var entry = Find(handle);
        if (!entry.Process.HasExited)
        {
            if (force || OperatingSystem.IsWindows())
            {
                ProcessExecution.KillOwnedTree(entry.Process);
            }
            else
            {
                if (kill(entry.Process.Id, 15) != 0 && !entry.Process.HasExited)
                {
                    throw new IOException("无法向游戏进程发送退出信号。");
                }
            }
        }

        await entry.Completion.WaitAsync(ct)
            .ConfigureAwait(false);
    }

    // 有界队列保留最近 256 条，文件日志独立持久化（超长行省略、文件达到上限后显式截断）。
    public async IAsyncEnumerable<ProcessLogLine> ReadAsync(GameProcessHandle handle, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var entry = Find(handle);
        if (entry.Detached)
        {
            throw new NotSupportedException("独立进程日志使用文件路径读取，不提供托管管道订阅。");
        }

        await foreach (var item in entry.Logs.Reader.ReadAllAsync(ct)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public async Task ReleaseAsync(GameProcessHandle handle)
    {
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(handle.Id, out entry!))
            {
                return;
            }

            if (entry.Handle != handle)
            {
                throw new InvalidOperationException("游戏句柄身份不匹配。");
            }
        }

        if (!entry.Completion.IsCompleted)
        {
            throw new InvalidOperationException("游戏仍在运行，不能释放其观察句柄。");
        }

        await entry.Completion.ConfigureAwait(false);
        lock (gate)
        {
            if (entries.Remove(handle.Id))
            {
                entry.Process.Dispose();
            }
        }
    }

    // 关闭 Host 不等于停止游戏；活跃观察继续到退出，独立游戏不依赖本进程的输出管道。
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var entry in entries.Values.ToArray())
            {
                if (entry.Completion.IsCompleted)
                {
                    entry.Process.Dispose();
                    entries.Remove(entry.Handle.Id);
                }
            }
        }
    }

    private Entry Find(GameProcessHandle handle)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(handle.Id, out var entry) || entry.Handle.Pid != handle.Pid)
            {
                throw new InvalidOperationException("未知或已释放的游戏句柄。");
            }

            return entry;
        }
    }

    private async Task<int> MonitorAsync(Entry entry, IReadOnlyList<string> secrets)
    {
        try
        {
            if (entry.Detached)
            {
                await entry.Process.WaitForExitAsync()
                    .ConfigureAwait(false);
            }
            else
            {
                var writer = entry.Writer!;
                var sync = new object ();
                long sequence = 0;
                long written = 0;
                bool clipped = false;
                void Emit(string stream, string text)
                {
                    text = ProcessExecution.Redact(text, secrets);
                    lock (sync)
                    {
                        if (!clipped)
                        {
                            try
                            {
                                var size = System.Text.Encoding.UTF8.GetByteCount(text) + 1;
                                if (written + size < 16 * 1024 * 1024)
                                {
                                    writer.WriteLine(text);
                                    written += size;
                                }
                                else
                                {
                                    clipped = true;
                                    writer.WriteLine("<game log truncated at 16 MiB>");
                                }
                            }
                            catch (IOException)
                            {
                                clipped = true;
                                entry.Logs.Writer.TryWrite(
                                    new(++sequence, "meta", "<game log file write failed>")
                                );
                            }
                        }

                        entry.Logs.Writer.TryWrite(
                            new(++sequence, stream, text)
                        );
                    }
                }

                var pipes = Task.WhenAll(
                    ProcessExecution.PumpAsync(entry.Process.StandardOutput, "stdout", Emit),
                    ProcessExecution.PumpAsync(entry.Process.StandardError, "stderr", Emit)
                );
                await entry.Process.WaitForExitAsync()
                    .ConfigureAwait(false);
                try
                {
                    await pipes.WaitAsync(
                        TimeSpan.FromSeconds(3)
                    )
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    entry.Process.StandardOutput.Dispose();
                    entry.Process.StandardError.Dispose();
                    try
                    {
                        await pipes.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }

                    entry.Logs.Writer.TryWrite(
                        new(++sequence, "meta", "<inherited output pipe drain timed out>")
                    );
                }
            }

            return entry.Process.ExitCode;
        }
        finally
        {
            try
            {
                entry.Writer?.Dispose();
            }
            catch (IOException)
            {
                entry.Logs.Writer.TryWrite(
                    new(long.MaxValue, "meta", "<game log close failed>")
                );
            }

            entry.Logs.Writer.TryComplete();
            lock (gate)
            {
                if (disposed && entries.Remove(entry.Handle.Id))
                {
                    entry.Process.Dispose();
                }
            }
        }
    }

    private sealed class Entry(
        GameProcessHandle handle,
        Process process,
        string logFile,
        bool detached,
        StreamWriter? writer
    )
    {
        internal GameProcessHandle Handle { get; } = handle;
        internal Process Process { get; } = process;
        internal string LogFile { get; } = logFile;
        internal StreamWriter? Writer { get; } = writer;
        internal bool Detached { get; } = detached;
        internal Task<int> Completion { get; set; } = Task.FromResult(-1);
        internal Channel<ProcessLogLine> Logs { get; } = Channel.CreateBounded<ProcessLogLine>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            }
        );
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
