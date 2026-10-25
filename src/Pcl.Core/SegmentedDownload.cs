using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：SegmentedDownloadReport 汇集 Completed、Segments、ReusedSegments、Parallelism、FallbackReason，供该模块传递状态。
public sealed record SegmentedDownloadReport(
    bool Completed,
    int Segments,
    int ReusedSegments,
    int Parallelism,
    string? FallbackReason = null
);
/// <summary>Optional bounded range workers. Only complete, self-hashed blocks survive restart; the authoritative whole-file hash remains mandatory.</summary>
public static class SegmentedDownload
{
    public const long MinimumFileSize = 32L * 1024 * 1024;
    public const int BlockSize = 4 * 1024 * 1024;
    private static readonly SemaphoreSlim Requests = new(16, 16);
    // [实现] 数据约定：BlockReceipt 汇集 Start、Length、Sha256，供该模块传递状态。
    private sealed record BlockReceipt(long Start, long Length, string Sha256);
    // 下载与网络管理：按来源与文件校验信息取得内容，跟踪进度、取消和失败，保留可恢复缓存。
    private sealed class RangeRejected(string message) : IOException(message);
    // [实现] Eligible 的实现：计算并返回 item.Size >= MinimumFileSize && item.Size <= 32L * 1024 * 1024 * 1024 && item.Url.Scheme is "ht…。
    public static bool Eligible(DownloadItem item) => item.Size >= MinimumFileSize && item.Size <= 32L * 1024 * 1024 * 1024 && item.Url.Scheme is "http" or "https" && item.Sha1 is not null && Regex.IsMatch(item.Sha1, "^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64}|[a-fA-F0-9]{128})$");
    // [实现] TryDownloadAsync 的实现：按 !Eligible(item) 分支处理；执行 parallelism = Math.Clamp(parallelism, 2, 8)；准备 var count = checked((int)((item.Size + BlockSize - 1) / BlockSize))。
    public static async Task<SegmentedDownloadReport> TryDownloadAsync(
        HttpClient client,
        DownloadItem item,
        IReadOnlyList<Uri> sources,
        int parallelism,
        Action<string> ensureSafePath,
        Func<int, CancellationToken, Task> flow,
        Action<long>? progress = null,
        CancellationToken ct = default
    )
    {
        if (!Eligible(item))
            return new(false, 0, 0, 0, "文件不满足大小或可信哈希条件。");
        parallelism = Math.Clamp(parallelism, 2, 8);
        var count = checked((int)((item.Size + BlockSize - 1) / BlockSize));
        var parent = item.Path + ".segments";
        ensureSafePath(parent);
        Directory.CreateDirectory(parent);
        var identity = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{item.Size}:{item.Sha1!.ToLowerInvariant()}:{BlockSize}"
                )
            )
        )
            .ToLowerInvariant();
        var directory = Path.Combine(parent, identity);
        ensureSafePath(directory);
        Directory.CreateDirectory(directory);
        // [实现] PathFor 的实现：准备 var path = Path.Combine( directory, index.ToString("D5") + suffix )；执行 ensureSafePath(path)；返回 path。
        string PathFor(int index, string suffix)
        {
            var path = Path.Combine(
                directory,
                index.ToString("D5") + suffix
            );
            ensureSafePath(path);
            return path;
        }

        // [实现] Length 的实现：计算并返回 Math.Min(BlockSize, item.Size - (long)index * BlockSize)。
        long Length(int index) => Math.Min(BlockSize, item.Size - (long)index * BlockSize);
        var complete = new bool[count];
        var received = new long[count];
        int reused = 0;
        long bytes = 0;
        for (int index = 0; index < count; index++)
        {
            // [实现] 每次迭代（index < count）：执行 ct.ThrowIfCancellationRequested()；准备 var path = PathFor(index, ".bin")；准备 var metadata = PathFor(index, ".json")。
            ct.ThrowIfCancellationRequested();
            var path = PathFor(index, ".bin");
            var metadata = PathFor(index, ".json");
            if (!File.Exists(path) || !File.Exists(metadata))
                continue;
            try
            {
                // [实现] 在异常保护范围内：按 new FileInfo(metadata).Length > 4096 分支处理；准备 var receipt = JsonConvert.DeserializeObject<BlockReceipt>( File.ReadAllText(metadata) )；按 receipt is null || receipt.Start != (long)index * BlockSize || receipt.Length != Length(index) … 分支处理。
                if (new FileInfo(metadata).Length > 4096)
                    throw new InvalidDataException("分块记录过大。");
                var receipt = JsonConvert.DeserializeObject<BlockReceipt>(
                    File.ReadAllText(metadata)
                );
                if (receipt is null || receipt.Start != (long)index * BlockSize || receipt.Length != Length(index) || new FileInfo(path).Length != receipt.Length)
                    continue;
                using var input = File.OpenRead(path);
                if (Convert.ToHexString(
                    SHA256.HashData(input)
                )
                    .ToLowerInvariant() != receipt.Sha256)
                    continue;
                complete[index] = true;
                received[index] = receipt.Length;
                bytes += receipt.Length;
                reused++;
            }
            catch (JsonException)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }
        }

        progress?.Invoke(bytes);
        Uri? source = null;
        EntityTagHeaderValue? validator = null;
        if (reused != count)
        {
            // [实现] 条件成立（reused != count）时：遍历 sources 处理 candidate；按 source is null 分支处理。
            foreach (var candidate in sources)
            {
                // [实现] 对 sources 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 await Requests.WaitAsync(ct)；执行受异常处理保护的操作。
                ct.ThrowIfCancellationRequested();
                await Requests.WaitAsync(ct);
                try
                {
                    // [实现] 在异常保护范围内：准备 var request = Request(candidate, 0, 0, null)；准备 var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)；按 !ValidRange(response, 0, 0, item.Size) 分支处理。
                    using var request = Request(candidate, 0, 0, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!ValidRange(response, 0, 0, item.Size))
                        continue;
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    var probe = new byte[2];
                    int used = 0, read;
                    while (used < probe.Length && (read = await stream.ReadAsync(
                        probe.AsMemory(used),
                        ct
                    )) > 0)
                    {
                        // [实现] 循环满足 used < probe.Length && (read = await stream.ReadAsync( probe.AsMemory(used), ct )) > 0 时：执行 await flow(read, ct)；执行 used += read。
                        await flow(read, ct);
                        used += read;
                    }

                    if (used != 1)
                        continue;
                    source = candidate;
                    if (response.Headers.ETag is { IsWeak: false } etag)
                        validator = etag;
                    break;
                }
                catch (HttpRequestException)
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                }
                finally
                {
                    // [实现] 无论成功或失败，退出前：执行 Requests.Release()。
                    Requests.Release();
                }
            }

            if (source is null)
                return new(false, count, reused, parallelism, "服务器不支持可靠 Range，使用顺序下载。");
        }

        var merged = item.Path + ".segments-merged.part";
        ensureSafePath(merged);
        try
        {
            // Workers claim the next unfinished block as soon as they finish, so slow chunks do not pin fixed file partitions.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, count)
                    .Where(
                        x => !complete[x]
                    ),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = ct
                },
                async (index, token) =>
                {
                    // [实现] 回调触发时：准备 var start = (long)index * BlockSize；准备 var length = Length(index)；准备 var end = start + length - 1。
                    var start = (long)index * BlockSize;
                    var length = Length(index);
                    var end = start + length - 1;
                    for (int attempt = 0;; attempt++)
                    {
                        // [实现] 每次迭代（空）：执行 token.ThrowIfCancellationRequested()；准备 var partial = PathFor(index, ".part")；准备 var previous = Interlocked.Exchange(ref received[index], 0)。
                        token.ThrowIfCancellationRequested();
                        var partial = PathFor(index, ".part");
                        var previous = Interlocked.Exchange(ref received[index], 0);
                        if (previous != 0)
                            Interlocked.Add(ref bytes, -previous);
                        try
                        {
                            // [实现] 在异常保护范围内：执行 await Requests.WaitAsync(token)；执行受异常处理保护的操作。
                            await Requests.WaitAsync(token);
                            try
                            {
                                // [实现] 在异常保护范围内：准备 var request = Request(source!, start, end, validator)；准备 var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)；按 !ValidRange(response, start, end, item.Size) 分支处理。
                                using var request = Request(source!, start, end, validator);
                                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                                if (!ValidRange(response, start, end, item.Size))
                                    throw new RangeRejected("分块 Content-Range 不匹配，使用顺序下载。");
                                ensureSafePath(partial);
                                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                                {
                                    // [实现] 本段流程：准备 var input = await response.Content.ReadAsStreamAsync(token)；准备 var buffer = new byte[65536]；准备 int read。
                                    await using var input = await response.Content.ReadAsStreamAsync(token);
                                    var buffer = new byte[65536];
                                    int read;
                                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                                    {
                                        // [实现] 循环满足 (read = await input.ReadAsync(buffer, token)) > 0 时：按 output.Length + read > length 分支处理；执行 await flow(read, token)；执行 hash.AppendData(buffer, 0, read)。
                                        if (output.Length + read > length)
                                            throw new RangeRejected("分块响应超过请求大小，使用顺序下载。");
                                        await flow(read, token);
                                        hash.AppendData(buffer, 0, read);
                                        await output.WriteAsync(
                                            buffer.AsMemory(0, read),
                                            token
                                        );
                                        Interlocked.Add(ref received[index], read);
                                        progress?.Invoke(
                                            Interlocked.Add(ref bytes, read)
                                        );
                                    }

                                    if (output.Length != length)
                                        throw new IOException("分块响应不完整。");
                                }

                                token.ThrowIfCancellationRequested();
                                var target = PathFor(index, ".bin");
                                ensureSafePath(partial);
                                File.Move(partial, target, true);
                                JsonFiles.Write(
                                    PathFor(index, ".json"),
                                    new BlockReceipt(
                                        start,
                                        length,
                                        Convert.ToHexString(
                                            hash.GetHashAndReset()
                                        )
                                            .ToLowerInvariant()
                                    )
                                );
                                complete[index] = true;
                                break;
                            }
                            finally
                            {
                                // [实现] 无论成功或失败，退出前：执行 Requests.Release()。
                                Requests.Release();
                            }
                        }
                        catch (RangeRejected)
                        {
                            // [实现] 捕获 (RangeRejected) 后：重新抛出当前异常。
                            throw;
                        }
                        catch (Exception ex)when (ex is IOException or HttpRequestException or OperationCanceledException)
                        {
                            // [实现] 捕获 (Exception ex) 后：执行 token.ThrowIfCancellationRequested()；按 attempt >= 2 分支处理；执行 await Task.Delay(200 * (attempt + 1), token)。
                            token.ThrowIfCancellationRequested();
                            if (attempt >= 2)
                                throw;
                            await Task.Delay(200 * (attempt + 1), token);
                        }
                    }
                }
            );
            ensureSafePath(merged);
            await using (var output = new FileStream(merged, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                // [实现] 本段流程：在 index < count 条件下迭代。
                for (int index = 0; index < count; index++)
                {
                    // [实现] 每次迭代（index < count）：执行 ct.ThrowIfCancellationRequested()；准备 var input = File.OpenRead( PathFor(index, ".bin") )；执行 await input.CopyToAsync(output, ct)。
                    ct.ThrowIfCancellationRequested();
                    await using var input = File.OpenRead(
                        PathFor(index, ".bin")
                    );
                    await input.CopyToAsync(output, ct);
                }
            }

            if (!DownloadService.IsValid(item, merged))
            {
                // Individually intact blocks can still be the wrong remote content; discard those receipts before falling back.
                for (int index = 0; index < count; index++)
                {
                    // [实现] 每次迭代（index < count）：执行 File.Delete( PathFor(index, ".json") )；执行 File.Delete( PathFor(index, ".bin") )。
                    File.Delete(
                        PathFor(index, ".json")
                    );
                    File.Delete(
                        PathFor(index, ".bin")
                    );
                }

                ensureSafePath(merged);
                File.Delete(merged);
                return new(false, count, reused, parallelism, "分段结果未通过完整哈希校验，使用顺序下载。");
            }

            ct.ThrowIfCancellationRequested();
            ensureSafePath(item.Path);
            ensureSafePath(merged);
            File.Move(merged, item.Path, true);
            // Cleanup failure must not report a verified, committed target as a failed download.
            try
            {
                // [实现] 在异常保护范围内：在 index < count 条件下迭代；按 !Directory.EnumerateFileSystemEntries(directory) .Any() 分支处理；按 !Directory.EnumerateFileSystemEntries(parent) .Any() 分支处理。
                for (int index = 0; index < count; index++)
                    foreach (var suffix in new[]
                    {
                        ".bin",
                        ".json",
                        ".part"
                    }

                    )
                        File.Delete(
                            PathFor(index, suffix)
                        );
                if (!Directory.EnumerateFileSystemEntries(directory)
                    .Any())
                    Directory.Delete(directory);
                if (!Directory.EnumerateFileSystemEntries(parent)
                    .Any())
                    Directory.Delete(parent);
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 System.Diagnostics.Trace.WriteLine("Segment cleanup: " + ex.Message)。
                System.Diagnostics.Trace.WriteLine("Segment cleanup: " + ex.Message);
            }

            return new(true, count, reused, parallelism);
        }
        catch (RangeRejected ex)
        {
            // [实现] 捕获 (RangeRejected ex) 后：返回 new(false, count, reused, parallelism, ex.Message)。
            return new(false, count, reused, parallelism, ex.Message);
        }
        catch (HttpRequestException)
        {
            // [实现] 捕获 (HttpRequestException) 后：执行 ct.ThrowIfCancellationRequested()；返回 new(false, count, reused, parallelism, "分段请求失败，使用顺序下载。")。
            ct.ThrowIfCancellationRequested();
            return new(false, count, reused, parallelism, "分段请求失败，使用顺序下载。");
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 ensureSafePath(merged)；按 File.Exists(merged) 分支处理。
            ensureSafePath(merged);
            if (File.Exists(merged))
                File.Delete(merged);
        }
    }

    // [实现] Request 的实现：准备 var request = new HttpRequestMessage(HttpMethod.Get, uri)；执行 request.Headers.Range = new RangeHeaderValue(from, to)；执行 request.Headers.AcceptEncoding.ParseAdd("identity")。
    private static HttpRequestMessage Request(Uri uri, long from, long to, EntityTagHeaderValue? validator)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (validator is not null)
            request.Headers.IfRange = new RangeConditionHeaderValue(validator);
        return request;
    }

    // [实现] ValidRange 的实现：计算并返回 response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange i…。
    private static bool ValidRange(HttpResponseMessage response, long from, long to, long length) => response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { Unit: "bytes" } range && range.From == from && range.To == to && range.Length == length && (response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == to - from + 1) && !response.Content.Headers.ContentEncoding.Any(
        x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)
    );
}
