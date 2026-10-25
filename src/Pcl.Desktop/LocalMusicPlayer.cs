using System.Diagnostics;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
/// <summary>管理本地播放列表。取消令牌保证停止后不会自动启动下一首；只终止自己创建的 afplay。</summary>
public sealed class LocalMusicPlayer : IDisposable
{
    private readonly object state = new();
    private CancellationTokenSource? lifetime;
    private Task? playback;
    public string? CurrentFile { get; private set; }
    public int TracksStarted { get; private set; }
    public string? LastError { get; private set; }
    public bool IsPlaying => CurrentFile is not null;

    // [实现] Play 的实现：执行 Stop()；按 !OperatingSystem.IsMacOS() 分支处理；准备 var files = AppearanceFeatures.MusicFiles(settings)。
    public void Play(AppearanceSettings settings)
    {
        Stop();
        if (!OperatingSystem.IsMacOS())
        {
            // [实现] 条件成立（!OperatingSystem.IsMacOS()）时：结束当前过程。
            return;
        }

        var files = AppearanceFeatures.MusicFiles(settings);
        foreach (var file in files)
        {
            // [实现] 对 files 中的每一项：按 !File.Exists(file) 分支处理。
            if (!File.Exists(file))
            {
                // [实现] 条件成立（!File.Exists(file)）时：抛出 new FileNotFoundException("音乐文件不存在。", file)。
                throw new FileNotFoundException("音乐文件不存在。", file);
            }
        }

        if (files.Length == 0)
        {
            // [实现] 条件成立（files.Length == 0）时：结束当前过程。
            return;
        }

        var source = new CancellationTokenSource();
        lifetime = source;
        LastError = null;
        playback = Task.Run(
            async () =>
            {
                // [实现] 回调触发时：准备 int index = 0；准备 string? previous = null；执行受异常处理保护的操作。
                int index = 0;
                string? previous = null;
                try
                {
                    // [实现] 在异常保护范围内：在 !source.IsCancellationRequested 条件下循环。
                    while (!source.IsCancellationRequested)
                    {
                        // [实现] 循环满足 !source.IsCancellationRequested 时：准备 var candidates = files.Where( x => x != previous ) .ToArray()；准备 var file = settings.MusicShuffle && candidates.Length > 0 ? candidates[Random.Shared.Next(candi…；执行 previous = file。
                        var candidates = files.Where(
                            x => x != previous
                        )
                            .ToArray();
                        var file = settings.MusicShuffle && candidates.Length > 0
                            ? candidates[Random.Shared.Next(candidates.Length)]
                            : files[index++ % files.Length];
                        previous = file;
                        var start = new ProcessStartInfo("/usr/bin/afplay")
                        {
                            UseShellExecute = false,
                            RedirectStandardError = true
                        };
                        start.ArgumentList.Add("-v");
                        start.ArgumentList.Add(
                            settings.MusicVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        );
                        start.ArgumentList.Add(file);
                        using var process = Process.Start(start) ?? throw new IOException("无法启动音频播放。");
                        lock (state)
                        {
                            // [实现] 本段流程：按 ReferenceEquals(lifetime, source) && !source.IsCancellationRequested 分支处理。
                            if (ReferenceEquals(lifetime, source) && !source.IsCancellationRequested)
                            {
                                // [实现] 条件成立（ReferenceEquals(lifetime, source) && !source.IsCancellationRequested）时：执行 CurrentFile = file；执行 TracksStarted++。
                                CurrentFile = file;
                                TracksStarted++;
                            }
                        }

                        using var stop = source.Token.Register(
                            () =>
                            {
                                // [实现] 回调触发时：执行受异常处理保护的操作。
                                try
                                {
                                    // [实现] 在异常保护范围内：按 !process.HasExited 分支处理。
                                    if (!process.HasExited)
                                    {
                                        // [实现] 条件成立（!process.HasExited）时：执行 process.Kill()。
                                        process.Kill();
                                    }
                                }
                                catch (InvalidOperationException)
                                {
                                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                                }
                            }
                        );
                        var error = process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (!source.IsCancellationRequested && process.ExitCode != 0)
                        {
                            // [实现] 条件成立（!source.IsCancellationRequested && process.ExitCode != 0）时：抛出 new IOException("音频播放失败：" + await error)。
                            throw new IOException("音频播放失败：" + await error);
                        }

                        if (settings.MusicDirectory.Length == 0)
                        {
                            // [实现] 条件成立（settings.MusicDirectory.Length == 0）时：执行 break;。
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // [实现] 捕获 (Exception ex) 后：锁定 state 后访问共享状态。
                    lock (state)
                    {
                        // [实现] 本段流程：按 ReferenceEquals(lifetime, source) && !source.IsCancellationRequested 分支处理。
                        if (ReferenceEquals(lifetime, source) && !source.IsCancellationRequested)
                        {
                            // [实现] 条件成立（ReferenceEquals(lifetime, source) && !source.IsCancellationRequested）时：执行 LastError = ex.Message。
                            LastError = ex.Message;
                        }
                    }
                }
                finally
                {
                    // [实现] 无论成功或失败，退出前：锁定 state 后访问共享状态。
                    lock (state)
                    {
                        // [实现] 本段流程：按 ReferenceEquals(lifetime, source) 分支处理。
                        if (ReferenceEquals(lifetime, source))
                        {
                            // [实现] 条件成立（ReferenceEquals(lifetime, source)）时：执行 CurrentFile = null。
                            CurrentFile = null;
                        }
                    }
                }
            }
        );
    }

    // [实现] Stop 的实现：准备 var old = lifetime；执行 old?.Cancel()；按 old is not null 分支处理。
    public void Stop()
    {
        var old = lifetime;
        old?.Cancel();
        // 本地播放器等待进程结束，不阻塞 UI；清理令牌由该播放任务结束后完成。
        if (old is not null)
        {
            // [实现] 条件成立（old is not null）时：执行 _ = (playback ?? Task.CompletedTask).ContinueWith( _ => old.Dispose(), TaskScheduler.Default )。
            _ = (playback ?? Task.CompletedTask).ContinueWith(
                _ => old.Dispose(),
                TaskScheduler.Default
            );
        }

        lock (state)
        {
            // [实现] 本段流程：执行 lifetime = null；执行 CurrentFile = null。
            lifetime = null;
            CurrentFile = null;
        }
    }

    // [实现] Dispose 的实现：执行 Stop()。
    public void Dispose() => Stop();
}
