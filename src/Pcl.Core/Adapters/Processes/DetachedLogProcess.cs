using System.Diagnostics;
using System.Text;
using Pcl.Core.Ports.Processes;

namespace Pcl.Core.Adapters.Processes;

internal static class DetachedLogProcess
{
    internal static Process Start(ProcessSpec spec, string log)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("独立游戏日志当前使用 Unix Adapter。");
        var secrets = spec.SensitiveValues.Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal).Select(Encoding.UTF8.GetBytes).ToArray();
        if (secrets.Length > 4096 || secrets.Sum(x => (long)x.Length) > 1024 * 1024)
            throw new ArgumentException("独立日志脱敏配置过大。", nameof(spec));

        // Private, unique extraction; the companion removes itself after log drain.
        // macOS may kill a newly started signed executable if it is unlinked too early.
        var directory = Path.Combine(Path.GetTempPath(), "pcl-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var executable = Path.Combine(directory, "pcl-detached-log");
        Process? process = null;
        bool started = false;
        try
        {
            using (var resource = typeof(DetachedLogProcess).Assembly.GetManifestResourceStream("Pcl.DetachedLog")
                ?? throw new PlatformNotSupportedException("此构建不包含 Unix 独立日志辅助程序。"))
            using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = spec.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                RedirectStandardOutput = true
            };
            info.ArgumentList.Add(log);
            info.ArgumentList.Add(spec.Executable);
            foreach (var arg in spec.Arguments) info.ArgumentList.Add(arg);
            if (spec.Environment is not null)
                foreach (var (key, value) in spec.Environment) info.Environment[key] = value;
            process = Process.Start(info) ?? throw new IOException("无法创建独立游戏进程。");
            using (var config = new BinaryWriter(process.StandardInput.BaseStream, Encoding.UTF8, leaveOpen: false))
            {
                config.Write(secrets.Length);
                foreach (var secret in secrets) { config.Write(secret.Length); config.Write(secret); }
            }
            started = true;
            return process;
        }
        catch
        {
            if (process is not null) { ProcessExecution.KillOwnedTree(process); process.Dispose(); }
            throw;
        }
        finally
        {
            foreach (var secret in secrets) System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
            if (!started)
            {
                File.Delete(executable);
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
        }
    }
}
