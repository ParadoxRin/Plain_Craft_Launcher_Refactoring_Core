using System.Net;
using System.Net.Http.Headers;
using MeloongCore;

namespace Pcl.Core;
// Extracted service boundary for PCL's download -> temp -> check -> promote pipeline.
// Uses cancellable modern HTTP instead of the WPF/global-state coupled ModNet.
public sealed class DownloadService : IDisposable
{
    private HttpClient client;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> targetLocks = new(StringComparer.Ordinal);
    public DownloadTasks? Tasks { get; }
    public SegmentedDownloadReport? LastSegmentedDownload { get; private set; }

    private NetworkOptions options;
    // [实现] DownloadService 构造 的实现：执行 Tasks = paths is null ? null : DownloadTasks.For(paths)；执行 options = Tasks?.Options ?? new()；执行 client = handler is null ? new HttpClient( options.CreateHandler() ) : new HttpClient(handler)。
    public DownloadService(HttpMessageHandler? handler = null, AppPaths? paths = null)
    {
        Tasks = paths is null
            ? null
            : DownloadTasks.For(paths);
        options = Tasks?.Options ?? new();
        client = handler is null
            ? new HttpClient(
                options.CreateHandler()
            )
            : new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.1");
    }

    // 仅在无下载的写授权交接点更换 Handler，保留服务对象，避免旧页面借用悬空的已释放实例。
    internal void ReconfigureIdle(NetworkOptions current)
    {
        if (options == current)
        {
            return;
        }

        var replacement = new HttpClient(
            current.CreateHandler()
        )
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        replacement.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.1");
        var previous = Interlocked.Exchange(ref client, replacement);
        options = current;
        previous.Dispose();
    }

    // [实现] IsValid 的实现：计算并返回 new FileChecker { ActualSize = item.Size, Hash = item.Sha1 }.Check(path ?? item.Path)is null。
    public static bool IsValid(DownloadItem item, string? path = null) => new FileChecker
    {
        ActualSize = item.Size,
        Hash = item.Sha1
    }.Check(path ?? item.Path)is null;
    // [实现] DownloadAsync 的实现：准备 var targetLock = targetLocks.GetOrAdd( Path.GetFullPath(item.Path), _ => new(1, 1) )；执行 await targetLock.WaitAsync(cancellationToken)；执行受异常处理保护的操作。
    public async Task DownloadAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        var targetLock = targetLocks.GetOrAdd(
            Path.GetFullPath(item.Path),
            _ => new(1, 1)
        );
        await targetLock.WaitAsync(cancellationToken);
        try
        {
            // [实现] 在异常保护范围内：执行 await DownloadLockedAsync(item, cancellationToken)。
            await DownloadLockedAsync(item, cancellationToken);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 targetLock.Release()。
            targetLock.Release();
        }
    }

    // [实现] DownloadLockedAsync 的实现：执行 EnsureSafePath(item.Path)；执行 EnsureSafePath(item.Path + ".part")；按 IsValid(item) 分支处理。
    private async Task DownloadLockedAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        EnsureSafePath(item.Path);
        EnsureSafePath(item.Path + ".part");
        if (IsValid(item))
            return;
        var current = Tasks?.Options ?? options;
        if (current.ArtificialDelayMs > 0)
        {
            // [实现] 条件成立（current.ArtificialDelayMs > 0）时：执行 await Task.Delay(current.ArtificialDelayMs, cancellationToken)。
            await Task.Delay(current.ArtificialDelayMs, cancellationToken);
        }

        if (!current.DisableLocalCopy && Tasks is not null && !string.IsNullOrEmpty(item.Sha1))
        {
            // [实现] 条件成立（!current.DisableLocalCopy && Tasks is not null && !string.IsNullOrEmpty(item.Sha1)）时：准备 var reusable = Tasks.ReusableCandidates(item).FirstOrDefault(x => File.Exists(x.Path) && (File.…；按 reusable is not null 分支处理。
            var reusable = Tasks.ReusableCandidates(item)
                .FirstOrDefault(
                    x => File.Exists(x.Path) && (File.GetAttributes(x.Path) & FileAttributes.ReparsePoint) == 0 && IsValid(item, x.Path)
                );
            if (reusable is not null)
            {
                // [实现] 条件成立（reusable is not null）时：执行 Directory.CreateDirectory( Path.GetDirectoryName(item.Path)! )；执行 EnsureSafePath(reusable.Path)；执行 cancellationToken.ThrowIfCancellationRequested()。
                Directory.CreateDirectory(
                    Path.GetDirectoryName(item.Path)!
                );
                EnsureSafePath(reusable.Path);
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(reusable.Path, item.Path + ".part", true);
                if (!IsValid(item, item.Path + ".part"))
                {
                    // [实现] 条件成立（!IsValid(item, item.Path + ".part")）时：抛出 new IOException("缓存复制校验失败。")。
                    throw new IOException("缓存复制校验失败。");
                }

                File.Move(item.Path + ".part", item.Path, true);
                return;
            }
        }

        if (current.DebugMode && Tasks is not null)
        {
            // [实现] 条件成立（current.DebugMode && Tasks is not null）时：准备 var entry = $"{DateTimeOffset.UtcNow:O} download host={item.Url.Host} file={Path.GetFileName(it…；锁定 targetLocks 后访问共享状态。
            var entry = $"{DateTimeOffset.UtcNow:O} download host={item.Url.Host} file={Path.GetFileName(item.Path)} bytes={item.Size}\n";
            lock (targetLocks)
            {
                // [实现] 本段流程：执行 File.AppendAllText( Path.Combine(Tasks.Paths.Evidence, "download-debug.log"), entry )。
                File.AppendAllText(
                    Path.Combine(Tasks.Paths.Evidence, "download-debug.log"),
                    entry
                );
            }
        }

        if (item.Url.IsFile)
            throw new FileNotFoundException("安装器生成文件缺失或损坏，请修复加载器实例。", item.Path);
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(item.Path)!
        );
        var partial = item.Path + ".part";
        var id = Tasks?.Queue(item);
        var entered = false;
        try
        {
            // [实现] 在异常保护范围内：按 Tasks is not null 分支处理；准备 var sources = (Tasks?.Options ?? options).Sources(item)；准备 var settings = Tasks?.Options ?? options。
            if (Tasks is not null)
            {
                // [实现] 条件成立（Tasks is not null）时：执行 await Tasks.EnterAsync(cancellationToken)；执行 entered = true。
                await Tasks.EnterAsync(cancellationToken);
                entered = true;
            }

            var sources = (Tasks?.Options ?? options).Sources(item);
            var settings = Tasks?.Options ?? options;
            if (settings.SegmentedDownloads && SegmentedDownload.Eligible(item))
            {
                // [实现] 条件成立（settings.SegmentedDownloads && SegmentedDownload.Eligible(item)）时：执行 LastSegmentedDownload = await SegmentedDownload.TryDownloadAsync( client, item, sources, settin…；按 LastSegmentedDownload.Completed 分支处理；按 id is not null 分支处理。
                LastSegmentedDownload = await SegmentedDownload.TryDownloadAsync(
                    client,
                    item,
                    sources,
                    settings.SegmentWorkers,
                    EnsureSafePath,
                    (bytes, ct) => Tasks?.FlowAsync(bytes, ct) ?? Task.CompletedTask,
                    bytes =>
                    {
                        // [实现] 回调触发时：按 id is not null 分支处理。
                        if (id is not null)
                            Tasks!.Report(id, item, "下载中", bytes);
                    },
                    cancellationToken
                );
                if (LastSegmentedDownload.Completed)
                {
                    // [实现] 条件成立（LastSegmentedDownload.Completed）时：按 id is not null 分支处理；结束当前过程。
                    if (id is not null)
                        Tasks!.Report(id, item, "已完成", item.Size);
                    return;
                }

                if (id is not null)
                    Tasks!.Report(id, item, "下载中", 0, LastSegmentedDownload.FallbackReason);
            }

            for (var attempt = 0;; attempt++)
            {
                // [实现] 每次迭代（空）：执行 cancellationToken.ThrowIfCancellationRequested()；执行受异常处理保护的操作。
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // [实现] 在异常保护范围内：准备 var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0；按 item.Size >= 0 && offset >= item.Size 分支处理；准备 var request = new HttpRequestMessage(HttpMethod.Get, sources[attempt % sources.Count])。
                    var offset = File.Exists(partial)
                        ? new FileInfo(partial).Length
                        : 0;
                    if (item.Size >= 0 && offset >= item.Size)
                    {
                        // [实现] 条件成立（item.Size >= 0 && offset >= item.Size）时：按 IsValid(item, partial) 分支处理；执行 File.Delete(partial)；执行 offset = 0。
                        if (IsValid(item, partial))
                        {
                            // [实现] 条件成立（IsValid(item, partial)）时：执行 File.Move(partial, item.Path, true)；按 id is not null 分支处理；结束当前过程。
                            File.Move(partial, item.Path, true);
                            if (id is not null)
                                Tasks!.Report(
                                    id,
                                    item,
                                    "已完成",
                                    new FileInfo(item.Path).Length
                                );
                            return;
                        }

                        File.Delete(partial);
                        offset = 0;
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, sources[attempt % sources.Count]);
                    if (offset > 0)
                        request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if ((int)response.StatusCode is 429 or 503)
                    {
                        // [实现] 条件成立（(int)response.StatusCode is 429 or 503）时：准备 var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTime…；按 retry > TimeSpan.Zero 分支处理。
                        var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                        if (retry > TimeSpan.Zero)
                            await Task.Delay(
                                TimeSpan.FromSeconds(
                                    Math.Min(60, retry.Value.TotalSeconds)
                                ),
                                cancellationToken
                            );
                    }

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // [实现] 条件成立（response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable）时：执行 File.Delete(partial)；抛出 new IOException("服务器拒绝续传范围。")。
                        File.Delete(partial);
                        throw new IOException("服务器拒绝续传范围。");
                    }

                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        // [实现] 条件成立（response.StatusCode == HttpStatusCode.PartialContent）时：按 response.Content.Headers.ContentRange?.From != offset 分支处理。
                        if (response.Content.Headers.ContentRange?.From != offset)
                        {
                            // [实现] 条件成立（response.Content.Headers.ContentRange?.From != offset）时：执行 File.Delete(partial)；抛出 new IOException("服务器返回错误的续传范围。")。
                            File.Delete(partial);
                            throw new IOException("服务器返回错误的续传范围。");
                        }
                    }
                    else
                    {
                        // [实现] 前一条件不成立时：执行 offset = 0。
                        offset = 0;
                    }

                    EnsureSafePath(partial);
                    await using (var output = new FileStream(
                        partial,
                        offset > 0
                            ? FileMode.Append
                            : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        65536,
                        true
                    ))
                    {
                        // [实现] 本段流程：准备 var input = await response.Content.ReadAsStreamAsync(cancellationToken)；准备 var buffer = new byte[65536]；准备 int read。
                        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                        var buffer = new byte[65536];
                        int read;
                        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            // [实现] 循环满足 (read = await input.ReadAsync(buffer, cancellationToken)) > 0 时：按 Tasks is not null 分支处理；按 item.Size >= 0 && output.Length + read > item.Size 分支处理；执行 await output.WriteAsync( buffer.AsMemory(0, read), cancellationToken )。
                            if (Tasks is not null)
                                await Tasks.FlowAsync(read, cancellationToken);
                            if (item.Size >= 0 && output.Length + read > item.Size)
                                throw new InvalidDataException("下载内容超出声明大小。");
                            await output.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken
                            );
                            if (id is not null)
                                Tasks!.Report(id, item, "下载中", output.Length);
                        }
                    }

                    if (!IsValid(item, partial))
                    {
                        // [实现] 条件成立（!IsValid(item, partial)）时：执行 File.Delete(partial)；抛出 new InvalidDataException("下载文件大小或哈希不匹配。")。
                        File.Delete(partial);
                        throw new InvalidDataException("下载文件大小或哈希不匹配。");
                    }

                    EnsureSafePath(item.Path);
                    EnsureSafePath(partial);
                    File.Move(partial, item.Path, true);
                    if (id is not null)
                        Tasks!.Report(
                            id,
                            item,
                            "已完成",
                            new FileInfo(item.Path).Length
                        );
                    return;
                }
                catch (Exception ex)when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 cancellationToken.ThrowIfCancellationRequested()；按 ex is InvalidDataException 分支处理；按 attempt >= 3 分支处理。
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ex is InvalidDataException)
                        File.Delete(partial);
                    if (attempt >= 3)
                        throw;
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            300 * Math.Pow(2, attempt)
                        ),
                        cancellationToken
                    );
                }
            }
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：按 id is not null 分支处理；重新抛出当前异常。
            if (id is not null)
                Tasks!.Report(
                    id,
                    item,
                    ex is OperationCanceledException
                        ? "已取消"
                        : "失败",
                    File.Exists(partial)
                        ? new FileInfo(partial).Length
                        : 0,
                    ex is OperationCanceledException
                        ? "可以重试文件，随后修复实例。"
                        : ex.Message
                );
            throw;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 entered 分支处理。
            if (entered)
                Tasks!.Leave();
        }
    }

    // [实现] EnsureSafePath 的实现：准备 var full = Path.GetFullPath(path)；准备 var boundary = Path.GetDirectoryName(full)!；按 Tasks is not null && full.StartsWith( Tasks.Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + P… 分支处理。
    private void EnsureSafePath(string path)
    {
        var full = Path.GetFullPath(path);
        var boundary = Path.GetDirectoryName(full)!;
        if (Tasks is not null && full.StartsWith(
            Tasks.Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        ))
            boundary = Tasks.Paths.Root;
        for (string? cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            // [实现] 每次迭代（cursor is not null）：按 new FileInfo(cursor).LinkTarget is not null 分支处理；按 cursor == boundary 分支处理。
            if (new FileInfo(cursor).LinkTarget is not null)
                throw new InvalidDataException("下载目标包含符号链接：" + cursor);
            if (cursor == boundary)
                break;
        }
    }

    // [实现] DownloadManyAsync 的实现：准备 var unique = files.DistinctBy( x => System.IO.Path.GetFullPath(x.Path), OperatingSystem.IsWindo…；准备 long bytes = 0；准备 int completed = 0。
    public async Task DownloadManyAsync(
        IReadOnlyList<DownloadItem> files,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        // A duplicate target must not be written concurrently.
        var unique = files.DistinctBy(
            x => System.IO.Path.GetFullPath(x.Path),
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal
        )
            .ToArray();
        long bytes = 0;
        int completed = 0;
        var total = unique.Sum(
            x => Math.Max(0, x.Size)
        );
        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Tasks?.Options.ConcurrentFiles ?? 8,
                CancellationToken = cancellationToken
            },
            async (item, token) =>
            {
                // [实现] 回调触发时：执行 await DownloadAsync(item, token)；准备 var currentBytes = Interlocked.Add( ref bytes, Math.Max(0, item.Size) )；准备 var done = Interlocked.Increment(ref completed)。
                await DownloadAsync(item, token);
                var currentBytes = Interlocked.Add(
                    ref bytes,
                    Math.Max(0, item.Size)
                );
                var done = Interlocked.Increment(ref completed);
                progress?.Report(
                    new("下载并校验", done, unique.Length, currentBytes, total)
                );
            }
        );
        Tasks?.Flush();
    }

    // 只读借用者释放网络资源时不写任务缓存，避免覆盖独立 Host 的新记录。
    public void DisposeWithoutFlush() => client.Dispose();
    // [实现] Dispose 的实现：执行 Tasks?.Flush()；执行 client.Dispose()。
    public void Dispose()
    {
        Tasks?.Flush();
        client.Dispose();
    }
}
