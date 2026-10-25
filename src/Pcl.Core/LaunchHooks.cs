using System.Diagnostics;

namespace Pcl.Core;
// [实现] 数据约定：LaunchHookSettings 汇集 Enabled、Executable、Arguments、Wait、TimeoutSeconds，供该模块传递状态。
public sealed record LaunchHookSettings(
    bool Enabled = false,
    string Executable = "",
    string Arguments = "",
    bool Wait = true,
    int TimeoutSeconds = 60
)
{
    // [实现] Validate 的实现：按 TimeoutSeconds is < 1 or > 600 分支处理；按 Enabled && (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable)) 分支处理；执行 _ = ArgumentText.Parse(Arguments)。
    public void Validate()
    {
        if (TimeoutSeconds is < 1 or > 600)
            throw new InvalidDataException("启动前命令超时需为 1–600 秒。");
        if (Enabled && (!Path.IsPathFullyQualified(Executable) || !File.Exists(Executable)))
            throw new InvalidDataException("请选择存在的本地可执行文件。");
        _ = ArgumentText.Parse(Arguments);
    }

    // [实现] Load 的实现：准备 var file = Path.Combine(paths.Root, "trusted-launch-hook.json")；准备 var value = File.Exists(file) ? JsonFiles.Read(file) .ToObject<LaunchHookSettings>() ?? new() :…；执行 value.Validate()。
    public static LaunchHookSettings Load(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, "trusted-launch-hook.json");
        var value = File.Exists(file)
            ? JsonFiles.Read(file)
                .ToObject<LaunchHookSettings>() ?? new()
            : new();
        value.Validate();
        return value;
    }

    // [实现] Save 的实现：执行 Validate()；执行 JsonFiles.Write( Path.Combine(paths.Root, "trusted-launch-hook.json"), this )。
    public void Save(AppPaths paths)
    {
        Validate();
        JsonFiles.Write(
            Path.Combine(paths.Root, "trusted-launch-hook.json"),
            this
        );
    }
}

/// <summary>Only launcher-local settings are trusted. Packs/settings imports never populate this file.</summary>
public sealed class LaunchHookRunner(Pcl.Core.Ports.Processes.IShellCommandRunner? commands = null) : IDisposable
{
    private readonly List<Process> children = [];
    // 等待型命令交给短命令 Port，超时取消后先回收进程；非等待型辅助程序沿用原 children 生命周期。
    public async Task RunAsync(
        LaunchHookSettings settings,
        string directory,
        Action<string>? log = null,
        CancellationToken ct = default
    )
    {
        settings.Validate();
        if (!settings.Enabled)
            return;
        if (settings.Wait)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(settings.TimeoutSeconds)
            );
            try
            {
                var result = await (commands ?? new Pcl.Core.Adapters.Processes.SystemShellCommandRunner()).RunAsync(
                    new(
                        settings.Executable,
                        ArgumentText.Parse(settings.Arguments),
                        directory
                    ),
                    new HookProgress(log),
                    timeout.Token
                )
                    .ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new IOException("启动前命令返回 " + result.ExitCode + "，游戏未启动。");
                }
            }
            catch (OperationCanceledException)when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("启动前命令超时，游戏未启动。");
            }

            return;
        }

        var info = new ProcessStartInfo(settings.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in ArgumentText.Parse(settings.Arguments))
            info.ArgumentList.Add(arg);
        var process = new Process
        {
            StartInfo = info
        };
        process.OutputDataReceived += (_, e) =>
        {
            // [实现] 回调触发时：按 e.Data is not null 分支处理。
            if (e.Data is not null)
                log?.Invoke(
                    DiagnosticsService.Redact(e.Data)
                );
        };
        process.ErrorDataReceived += (_, e) =>
        {
            // [实现] 回调触发时：按 e.Data is not null 分支处理。
            if (e.Data is not null)
                log?.Invoke(
                    DiagnosticsService.Redact(e.Data)
                );
        };
        try
        {
            // [实现] 在异常保护范围内：执行 process.Start()；执行 children.Add(process)；执行 process.BeginOutputReadLine()。
            process.Start();
            children.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            // [实现] 捕获 空 后：执行 process.Dispose()；重新抛出当前异常。
            process.Dispose();
            throw;
        }
    }

    private sealed class HookProgress(Action<string>? log) : IProgress<Pcl.Core.Ports.Processes.ProcessLogLine>
    {
        public void Report(Pcl.Core.Ports.Processes.ProcessLogLine value) => log?.Invoke(
            DiagnosticsService.Redact(value.Text)
        );
    }

    // [实现] Dispose 的实现：遍历 children 处理 process；执行 children.Clear()。
    public void Dispose()
    {
        foreach (var process in children)
        {
            // [实现] 对 children 中的每一项：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：按 !process.HasExited 分支处理。
                if (!process.HasExited)
                    process.Kill(true);
            }
            finally
            {
                // [实现] 无论成功或失败，退出前：执行 process.Dispose()。
                process.Dispose();
            }
        }

        children.Clear();
    }
}
