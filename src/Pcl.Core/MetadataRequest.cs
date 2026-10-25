using System.Net;

namespace Pcl.Core;
/// <summary>Retry transient metadata failures without replacing cache until JSON parsing succeeds.</summary>
internal static class MetadataRequest
{
    // [实现] GetAsync 的实现：在 空 条件下迭代。
    internal static async Task<string> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            // [实现] 每次迭代（空）：准备 TimeSpan? retryAfter = null；准备 var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)；执行 timeout.CancelAfter( TimeSpan.FromSeconds(45) )。
            TimeSpan? retryAfter = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(45)
            );
            var requestToken = timeout.Token;
            try
            {
                // [实现] 在异常保护范围内：准备 var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToke…；按 response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)… 分支处理。
                RuntimeTrace.Write("metadata.request", attempt);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken);
                RuntimeTrace.Write("metadata.response", (int)response.StatusCode);
                if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    // [实现] 条件成立（response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)…）时：按 attempt == 2 分支处理；准备 var header = response.Headers.RetryAfter；执行 retryAfter = header?.Delta ?? (header?.Date is { } when ? when - DateTimeOffset.UtcNow : null)。
                    if (attempt == 2)
                        response.EnsureSuccessStatusCode();
                    var header = response.Headers.RetryAfter;
                    retryAfter = header?.Delta ?? (header?.Date is { } when
                        ? when - DateTimeOffset.UtcNow
                        : null);
                }
                else
                {
                    // [实现] 前一条件不成立时：执行 response.EnsureSuccessStatusCode()；按 response.Content.Headers.ContentLength > 16 * 1024 * 1024 分支处理；准备 var stream = await response.Content.ReadAsStreamAsync(requestToken)。
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > 16 * 1024 * 1024)
                        throw new InvalidDataException("元数据超过 16 MiB。");
                    await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                    using var buffer = new MemoryStream();
                    var block = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(block, requestToken)) > 0)
                    {
                        // [实现] 循环满足 (count = await stream.ReadAsync(block, requestToken)) > 0 时：按 buffer.Length + count > 16 * 1024 * 1024 分支处理；执行 buffer.Write(block, 0, count)。
                        if (buffer.Length + count > 16 * 1024 * 1024)
                            throw new InvalidDataException("元数据超过 16 MiB。");
                        buffer.Write(block, 0, count);
                    }

                    return System.Text.Encoding.UTF8.GetString(
                        buffer.ToArray()
                    );
                }
            }
            catch (HttpRequestException ex)when (attempt < 2 && (ex.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int? )ex.StatusCode >= 500))
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }
            catch (IOException)when (attempt < 2)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }
            catch (OperationCanceledException)when (!ct.IsCancellationRequested && attempt < 2)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }

            ct.ThrowIfCancellationRequested();
            var wait = retryAfter ?? TimeSpan.FromMilliseconds(350 * (attempt + 1));
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    Math.Clamp(wait.TotalMilliseconds, 0, 30000)
                ),
                ct
            );
        }
    }
}
