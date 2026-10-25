using System.Diagnostics;
using System.Net.Http.Headers;

namespace Pcl.Core;
// [实现] 数据约定：NetworkProbeTarget 汇集 Name、Url，供该模块传递状态。
public sealed record NetworkProbeTarget(string Name, string Url)
{
    // [实现] ToString 的实现：计算并返回 Name + " · " + Url。
    public override string ToString() => Name + " · " + Url;
}

// [实现] 数据约定：NetworkProbeResult 汇集 Name、Url、Success、StatusCode、Bytes、ResponseMilliseconds、ElapsedMilliseconds、KiBPerSecond、RangeHonored、Error，供该模块传递状态。
public sealed record NetworkProbeResult(
    string Name,
    string Url,
    bool Success,
    int? StatusCode,
    long Bytes,
    double ResponseMilliseconds,
    double ElapsedMilliseconds,
    double KiBPerSecond,
    bool RangeHonored,
    string? Error = null
)
{
    // [实现] ToString 的实现：计算并返回 Success ? $"{Name} · 响应 {ResponseMilliseconds:F0} ms · {Bytes / 1024.0:F1} KiB / {KiBPerSecond:…。
    public override string ToString() => Success
        ? $"{Name} · 响应 {ResponseMilliseconds:F0} ms · {Bytes / 1024.0:F1} KiB / {KiBPerSecond:F1} KiB/s · HTTP {StatusCode}"
        : $"{Name} · {Error}";
}

/// <summary>Small explicit samples, separate from download queue and installation validation.</summary>
public sealed class NetworkDiagnosticsService : IDisposable
{
    public const int MaximumSampleBytes = 65536;
    private readonly HttpClient http;
    // [实现] NetworkDiagnosticsService 构造 的实现：执行 options.Validate()；按 handler is null 分支处理；执行 http = new(handler) { Timeout = Timeout.InfiniteTimeSpan }。
    public NetworkDiagnosticsService(NetworkOptions options, HttpMessageHandler? handler = null)
    {
        options.Validate();
        if (handler is null)
        {
            // [实现] 条件成立（handler is null）时：准备 var configured = options.CreateHandler()；执行 configured.AllowAutoRedirect = false；执行 handler = configured。
            var configured = options.CreateHandler();
            configured.AllowAutoRedirect = false;
            handler = configured;
        }

        http = new(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.8-NetworkCheck");
    }

    // [实现] ConfiguredTargets 的实现：准备 var tasks = DownloadTasks.For(paths)；准备 var options = tasks.Options；准备 var samples = tasks.Snapshot() .Select( x => x.Item.Url ) .Where( x => x.Scheme == "https" && x…。
    public static IReadOnlyList<NetworkProbeTarget> ConfiguredTargets(AppPaths paths)
    {
        var tasks = DownloadTasks.For(paths);
        var options = tasks.Options;
        var samples = tasks.Snapshot()
            .Select(
                x => x.Item.Url
            )
            .Where(
                x => x.Scheme == "https" && x.UserInfo.Length == 0 && x.Query.Length == 0 && x.Fragment.Length == 0
            )
            .ToArray();
        var targets = new List<NetworkProbeTarget>
        {
            new("官方版本清单", MinecraftService.ManifestUrl),
            new("官方 Java 清单", JavaService.RuntimeIndexUrl)
        };
        foreach (var host in new[]
        {
            "libraries.minecraft.net",
            "resources.download.minecraft.net",
            "piston-data.mojang.com"
        }

        )
            if (samples.FirstOrDefault(
                x => x.Host == host
            )is { } url)
                targets.Add(
                    new("官方文件样本 · " + host, url.AbsoluteUri)
                );
        if (options.MirrorBase.Length > 0)
        {
            // [实现] 条件成立（options.MirrorBase.Length > 0）时：准备 var original = samples.FirstOrDefault( x => x.Host.Equals(options.MirrorHost, StringComparison.…；按 original is not null 分支处理。
            var original = samples.FirstOrDefault(
                x => x.Host.Equals(options.MirrorHost, StringComparison.OrdinalIgnoreCase)
            );
            if (original is not null)
            {
                // [实现] 条件成立（original is not null）时：执行 targets.Add( new("镜像对应官方样本", original.AbsoluteUri) )；执行 targets.Add( new( "已配置镜像样本", options.MirrorBase.TrimEnd('/') + original.AbsolutePath ) )。
                targets.Add(
                    new("镜像对应官方样本", original.AbsoluteUri)
                );
                targets.Add(
                    new(
                        "已配置镜像样本",
                        options.MirrorBase.TrimEnd('/') + original.AbsolutePath
                    )
                );
            }
            else
                targets.Add(
                    new("已配置镜像根地址（只测可达性）", options.MirrorBase)
                );
        }

        return targets.DistinctBy(
            x => x.Url
        )
            .Take(8)
            .ToArray();
    }

    // [实现] ProbeAsync 的实现：按 !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserI… 分支处理；准备 var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct)；执行 timeout.CancelAfter( TimeSpan.FromSeconds(10) )。
    public async Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("测速地址须为不含账号、令牌或片段的 HTTPS 文件地址。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(10)
        );
        var watch = Stopwatch.StartNew();
        long bytes = 0;
        double responseMs = 0;
        int? status = null;
        bool range = false;
        try
        {
            // [实现] 在异常保护范围内：准备 var request = new HttpRequestMessage(HttpMethod.Get, uri)；执行 request.Headers.Range = new RangeHeaderValue(0, MaximumSampleBytes - 1)；执行 request.Headers.AcceptEncoding.Add( new StringWithQualityHeaderValue("identity") )。
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, MaximumSampleBytes - 1);
            request.Headers.AcceptEncoding.Add(
                new StringWithQualityHeaderValue("identity")
            );
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            responseMs = watch.Elapsed.TotalMilliseconds;
            status = (int)response.StatusCode;
            response.EnsureSuccessStatusCode();
            range = status == 206;
            if (range && (response.Content.Headers.ContentRange?.From != 0 || response.Content.Headers.ContentRange?.To > MaximumSampleBytes - 1))
                throw new InvalidDataException("服务器返回的样本范围不符。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            while (bytes < MaximumSampleBytes)
            {
                // [实现] 循环满足 bytes < MaximumSampleBytes 时：准备 var read = await stream.ReadAsync( buffer.AsMemory( 0, (int)Math.Min(buffer.Length, MaximumSamp…；按 read == 0 分支处理；执行 bytes += read。
                var read = await stream.ReadAsync(
                    buffer.AsMemory(
                        0,
                        (int)Math.Min(buffer.Length, MaximumSampleBytes - bytes)
                    ),
                    timeout.Token
                );
                if (read == 0)
                    break;
                bytes += read;
            }

            var elapsed = watch.Elapsed.TotalMilliseconds;
            return new(
                target.Name,
                uri.AbsoluteUri,
                true,
                status,
                bytes,
                responseMs,
                elapsed,
                bytes / 1024.0 / Math.Max(.001, elapsed / 1000),
                range
            );
        }
        catch (Exception ex)when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            // [实现] 捕获 (Exception ex) 后：执行 ct.ThrowIfCancellationRequested()；返回 new( target.Name, uri.AbsoluteUri, false, status, bytes, responseMs, watch.Elapsed.TotalMillise…。
            ct.ThrowIfCancellationRequested();
            return new(
                target.Name,
                uri.AbsoluteUri,
                false,
                status,
                bytes,
                responseMs,
                watch.Elapsed.TotalMilliseconds,
                0,
                range,
                ex is OperationCanceledException
                    ? "10 秒内未完成样本请求"
                    : status is >= 300 and < 400
                        ? "地址需要跳转，请使用最终 HTTPS 文件地址"
                        : ex.Message
            );
        }
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
