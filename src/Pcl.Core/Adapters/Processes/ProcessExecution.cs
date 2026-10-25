using System.Diagnostics;
using System.Text;
using Pcl.Core.Ports.Processes;

namespace Pcl.Core.Adapters.Processes;
/// <summary>进程创建与有界文本管道。只由 Adapter 使用，参数始终按 ArgumentList 传递。</summary>
internal static class ProcessExecution
{
    internal static Process Start(ProcessSpec spec, bool redirect)
    {
        var info = new ProcessStartInfo(spec.Executable)
        {
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect
        };
        foreach (var arg in spec.Arguments)
        {
            info.ArgumentList.Add(arg);
        }

        if (spec.Environment is not null)
        {
            foreach (var(key, value)in spec.Environment)
            {
                info.Environment[key] = value;
            }
        }

        var process = new Process
        {
            StartInfo = info
        };
        try
        {
            if (!process.Start())
            {
                throw new IOException("无法创建进程。");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    // 超长行完全省略，避免无换行输出无限占内存或把已截断的敏感值前缀发到日志。
    internal static async Task PumpAsync(StreamReader reader, string stream, Action<string, string> emit)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        bool oversized = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)
            .ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                char ch = buffer[i];
                if (ch == '\n')
                {
                    emit(
                        stream,
                        oversized
                            ? "<oversized output line omitted>"
                            : line.ToString()
                                .TrimEnd('\r')
                    );
                    line.Clear();
                    oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length >= 65536)
                    {
                        oversized = true;
                        line.Clear();
                    }
                    else
                    {
                        line.Append(ch);
                    }
                }
            }
        }

        if (oversized || line.Length > 0)
        {
            emit(
                stream,
                oversized
                    ? "<oversized output line omitted>"
                    : line.ToString()
            );
        }
    }

    internal static string Redact(string text, IReadOnlyList<string> values)
    {
        foreach (var secret in values.Where(
            x => x.Length > 5
        ))
        {
            text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        return text;
    }

    // 仅操作当前 Adapter 拥有的 Process 对象；不提供按任意 PID 杀进程的入口。
    internal static void KillOwnedTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
