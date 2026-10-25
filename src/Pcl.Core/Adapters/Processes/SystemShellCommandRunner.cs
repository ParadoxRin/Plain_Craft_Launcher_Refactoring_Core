using System.Text;
using Pcl.Core.Ports.Processes;

namespace Pcl.Core.Adapters.Processes;
/// <summary>双流并行排空；每条流最多保留 1,048,576 个 UTF-16 字符，取消会等待拥有的进程退出与管道收尾。</summary>
public sealed class SystemShellCommandRunner : IShellCommandRunner
{
    public async Task<CommandResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessLogLine>? output = null,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        using var process = ProcessExecution.Start(spec, true);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var gate = new object ();
        bool truncated = false;
        long sequence = 0;
        void Emit(string stream, string text)
        {
            text = ProcessExecution.Redact(text, spec.SensitiveValues);
            lock (gate)
            {
                if (text == "<oversized output line omitted>")
                {
                    truncated = true;
                }

                var target = stream == "stdout"
                    ? stdout
                    : stderr;
                if (target.Length + text.Length + 1 <= 1_048_576)
                {
                    target.AppendLine(text);
                }
                else
                {
                    truncated = true;
                }

                try
                {
                    output?.Report(
                        new(++sequence, stream, text)
                    );
                }
                catch (Exception)
                {
                    // 诊断订阅错误不能停止管道排空。
                }
            }
        }

        var pipes = Task.WhenAll(
            ProcessExecution.PumpAsync(process.StandardOutput, "stdout", Emit),
            ProcessExecution.PumpAsync(process.StandardError, "stderr", Emit)
        );
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(),
                pipes
            )
                .WaitAsync(ct)
                .ConfigureAwait(false);
            return new(
                process.ExitCode,
                stdout.ToString(),
                stderr.ToString(),
                truncated
            );
        }
        catch
        {
            ProcessExecution.KillOwnedTree(process);
            await process.WaitForExitAsync()
                .ConfigureAwait(false);
            // 极端情况下后代保留管道，关闭读取端以保证取消可以返回；不继续使用已释放的流。
            try
            {
                await pipes.WaitAsync(
                    TimeSpan.FromSeconds(3)
                )
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                try
                {
                    await pipes.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            throw;
        }
    }
}
