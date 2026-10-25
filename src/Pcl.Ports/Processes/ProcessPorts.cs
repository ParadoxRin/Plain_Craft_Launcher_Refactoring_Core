namespace Pcl.Core.Ports.Processes;
/// <summary>内部执行输入；避免 record 自动 ToString 输出敏感参数，不作为 UI 或 RPC DTO。</summary>
public sealed class ProcessSpec(string executable, IReadOnlyList<string> arguments, string workingDirectory)
{
    public string Executable { get; } = executable;
    public IReadOnlyList<string> Arguments { get; } = arguments;
    public string WorkingDirectory { get; } = workingDirectory;
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public IReadOnlyList<string> SensitiveValues { get; init; } = [];
}

public sealed record ProcessLogLine(long Sequence, string Stream, string Text);
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated);
/// <summary>短命令异步等待完成；取消执行会结束拥有的进程树，取消后完成清理再返回。</summary>
public interface IShellCommandRunner
{
    Task<CommandResult> RunAsync(
        ProcessSpec spec,
        IProgress<ProcessLogLine>? output = null,
        CancellationToken ct = default
    );
}

public sealed record GameProcessHandle(string Id, int Pid);
public sealed record GameProcessOptions(string LogFile, bool Detached = false);
/// <summary>长进程返回逻辑句柄；取消等待不杀游戏，终止和释放独立。日志流每个句柄一个消费者，有界保留。</summary>
public interface IGameProcessHost : IDisposable
{
    Task<GameProcessHandle> StartAsync(ProcessSpec spec, GameProcessOptions options, CancellationToken ct = default);
    bool IsRunning(GameProcessHandle handle);
    Task<int> WaitForExitAsync(GameProcessHandle handle, CancellationToken ct = default);
    Task TerminateAsync(GameProcessHandle handle, bool force = true, CancellationToken ct = default);
    IAsyncEnumerable<ProcessLogLine> ReadAsync(GameProcessHandle handle, CancellationToken ct = default);
    Task ReleaseAsync(GameProcessHandle handle);
}
