using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：NetworkOptions 汇集 ConcurrentFiles、SpeedKiB、Proxy、MirrorBase、MirrorHost、PreferMirror、SegmentedDownloads、SegmentWorkers，供该模块传递状态。
public sealed record NetworkOptions(
    int ConcurrentFiles = 8,
    int SpeedKiB = 0,
    string Proxy = "",
    string MirrorBase = "",
    string MirrorHost = "",
    bool PreferMirror = false,
    bool SegmentedDownloads = false,
    int SegmentWorkers = 4
)
{
    public string ManifestMirror { get; init; } = "";
    public string ManifestSource { get; init; } = "official";
    public string ResourceApiMirror { get; init; } = "";
    public string CurseForgeApiMirror { get; init; } = "";
    public string ResourceSource { get; init; } = "official";
    public string FileNameStyle { get; init; } = "original";
    public string ModNameStyle { get; init; } = "name";
    public bool NotifyRelease { get; init; }
    public bool NotifySnapshot { get; init; }
    public bool DisableLocalCopy { get; init; }
    public bool DebugMode { get; init; }
    public int ArtificialDelayMs { get; init; }

    // [实现] Validate 的实现：按 ManifestSource is not ("official" or "fallback" or "mirror") || ResourceSource is not ("officia… 分支处理；遍历 new[] { ManifestMirror, ResourceApiMirror } 处理 url；按 ConcurrentFiles is < 1 or > 32 || SpeedKiB is < 0 or > 1048576 分支处理。
    public void Validate()
    {
        if (ManifestSource is not ("official" or "fallback" or "mirror") || ResourceSource is not ("official" or "fallback" or "mirror") || FileNameStyle is not ("original" or "brackets" or "square" or "prefix" or "suffix") || ModNameStyle is not ("name" or "file") || ArtificialDelayMs is < 0 or > 5000)
        {
            // [实现] 条件成立（ManifestSource is not ("official" or "fallback" or "mirror") || ResourceSource is not ("officia…）时：抛出 new InvalidDataException("来源、文件名或调试设置无效。")。
            throw new InvalidDataException("来源、文件名或调试设置无效。");
        }

        foreach (var url in new[]
        {
            ManifestMirror,
            ResourceApiMirror,
            CurseForgeApiMirror
        }

        )
        {
            // [实现] 对 new[] { ManifestMirror, ResourceApiMirror } 中的每一项：按 url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" … 分支处理。
            if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            {
                // [实现] 条件成立（url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" …）时：抛出 new InvalidDataException("镜像必须使用不含凭据和查询参数的 HTTPS 地址。")。
                throw new InvalidDataException("镜像必须使用不含凭据和查询参数的 HTTPS 地址。");
            }
        }

        if (ConcurrentFiles is < 1 or > 32 || SpeedKiB is < 0 or > 1048576)
            throw new InvalidDataException("并发需要 1–32，限速需要 0–1048576 KiB/s（0 表示不限速）。");
        if (SegmentWorkers is < 2 or > 8)
            throw new InvalidDataException("单文件分段并发需要 2–8。");
        if (Proxy.Length > 0 && (!Uri.TryCreate(Proxy, UriKind.Absolute, out var p) || p.Scheme is not ("http" or "https" or "socks5") || p.UserInfo.Length > 0))
            throw new InvalidDataException("代理需要 http、https 或 socks5 地址，不在配置中保存代理密码。");
        if (MirrorBase.Length > 0 && (!Uri.TryCreate(MirrorBase, UriKind.Absolute, out var m) || m.Scheme != "https" || m.UserInfo.Length > 0 || m.Query.Length > 0 || Uri.CheckHostName(MirrorHost) == UriHostNameType.Unknown))
            throw new InvalidDataException("镜像需要 HTTPS 根地址和要替换的原始主机名。");
    }

    // [实现] Load 的实现：准备 var file = Path.Combine(paths.Root, "network.json")；按 !File.Exists(file) 分支处理；准备 var json = JsonFiles.Read(file)。
    public static NetworkOptions Load(AppPaths paths)
    {
        var file = Path.Combine(paths.Root, "network.json");
        if (!File.Exists(file))
            return new();
        var json = JsonFiles.Read(file);
        // Json.NET supplies zero for absent constructor arguments; preserve the 0.7 six-field configuration.
        if (json.GetValue(
            nameof(SegmentWorkers),
            StringComparison.OrdinalIgnoreCase
        )is null)
            json[nameof(SegmentWorkers)] = 4;
        var options = json.ToObject<NetworkOptions>() ?? new();
        options.Validate();
        return options;
    }

    // [实现] Save 的实现：执行 Validate()；执行 JsonFiles.Write( Path.Combine(paths.Root, "network.json"), this )。
    public void Save(AppPaths paths)
    {
        Validate();
        JsonFiles.Write(
            Path.Combine(paths.Root, "network.json"),
            this
        );
        RuntimeTrace.Configure(paths, DebugMode);
    }

    // [实现] CreateHandler 的实现：执行 Validate()；准备 var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), Conn…；按 Proxy.Length > 0 分支处理。
    public SocketsHttpHandler CreateHandler()
    {
        Validate();
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        if (Proxy.Length > 0)
            handler.Proxy = new WebProxy(Proxy);
        return handler;
    }

    // [实现] Sources 的实现：按 MirrorBase.Length == 0 || string.IsNullOrEmpty(item.Sha1) || !item.Url.Host.Equals(MirrorHost, … 分支处理；准备 var mirror = new Uri( MirrorBase.TrimEnd('/') + item.Url.PathAndQuery )；返回 PreferMirror ? new[] { mirror, item.Url } : new[] { item.Url, mirror }。
    public IReadOnlyList<Uri> Sources(DownloadItem item)
    {
        // Never use a mirror for content that cannot be checked against the original digest.
        if (MirrorBase.Length == 0 || string.IsNullOrEmpty(item.Sha1) || !item.Url.Host.Equals(MirrorHost, StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                item.Url
            };
        var mirror = new Uri(
            MirrorBase.TrimEnd('/') + item.Url.PathAndQuery
        );
        return PreferMirror
            ? new[]
            {
                mirror,
                item.Url
            }

            : new[]
            {
                item.Url,
                mirror
            };
    }
}

// [实现] 数据约定：DownloadTaskRow 汇集 Id、Item、State、Bytes、Updated、Error，供该模块传递状态。
public sealed record DownloadTaskRow(
    string Id,
    DownloadItem Item,
    string State,
    long Bytes,
    DateTimeOffset Updated,
    string? Error = null
)
{
    // [实现] ToString 的实现：计算并返回 $"{State} · {Path.GetFileName(Item.Path)} · {Bytes / 1048576.0:F1}/{Math.Max(0, Item.Size) / 10…。
    public override string ToString() => $"{State} · {Path.GetFileName(Item.Path)} · {Bytes / 1048576.0:F1}/{Math.Max(0, Item.Size) / 1048576.0:F1} MiB";
}

// Shared by all version services in one data root; requests for the same file serialize.
public sealed class DownloadTasks
{
    private static readonly ConcurrentDictionary<string, DownloadTasks> roots = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly Dictionary<string, DownloadTaskRow> rows = new();
    private readonly string file;
    private DateTimeOffset lastSaved;
    private long rateTicks;
    private int active;
    private bool paused;
    public AppPaths Paths { get; }
    public NetworkOptions Options { get; private set; }
    public Func<bool>? CanWrite { get; set; }

    // [实现] For 的实现：计算并返回 roots.GetOrAdd( paths.Root, _ => new(paths) )。
    public static DownloadTasks For(AppPaths paths) => roots.GetOrAdd(
        paths.Root,
        _ => new(paths)
    );
    // [实现] DownloadTasks 构造 的实现：执行 Paths = paths；执行 file = Path.Combine(paths.Root, "download-tasks.json")；执行 Options = NetworkOptions.Load(paths)。
    private DownloadTasks(AppPaths paths)
    {
        Paths = paths;
        file = Path.Combine(paths.Root, "download-tasks.json");
        Options = NetworkOptions.Load(paths);
        if (File.Exists(file))
        {
            // [实现] 条件成立（File.Exists(file)）时：遍历 JsonConvert.DeserializeObject<DownloadTaskRow[]>( File.ReadAllText(file) ) ?? [] 处理 row。
            foreach (var row in JsonConvert.DeserializeObject<DownloadTaskRow[]>(
                File.ReadAllText(file)
            ) ?? [])
            {
                // [实现] 对 JsonConvert.DeserializeObject<DownloadTaskRow[]>( File.ReadAllText(file) ) ?? [] 中的每一项：按 !IsInsideRoot(row.Item.Path) || row.Item.Url.Scheme is not ("http" or "https") 分支处理；执行 rows[row.Id] = row.State is "下载中" or "排队中" ? row with { State = "已中断", Error = "上次退出时未完成；可以重试文件…。
                if (!IsInsideRoot(row.Item.Path) || row.Item.Url.Scheme is not ("http" or "https"))
                    continue;
                rows[row.Id] = row.State is "下载中" or "排队中"
                    ? row with
                    {
                        State = "已中断",
                        Error = "上次退出时未完成；可以重试文件，随后修复实例。"
                    }

                    : row;
            }
        }
    }

    // 独立 Host 和兼容 UI 在排他交接后重读持久任务，避免旧进程缓存覆盖另一方的新任务。
    public void ReloadIdle()
    {
        lock (sync)
        {
            if (active != 0)
            {
                throw new InvalidOperationException("DOWNLOADS_ACTIVE");
            }

            var loaded = File.Exists(file)
                ? JsonConvert.DeserializeObject<DownloadTaskRow[]>(
                    File.ReadAllText(file)
                ) ?? []
                : [];
            rows.Clear();
            foreach (var row in loaded)
            {
                if (IsInsideRoot(row.Item.Path) && row.Item.Url.Scheme is "http" or "https")
                {
                    rows[row.Id] = row.State is "下载中" or "排队中"
                        ? row with
                        {
                            State = "已中断"
                        }

                        : row;
                }
            }

            Options = NetworkOptions.Load(Paths);
            rateTicks = 0;
        }
    }

    // [实现] IsInsideRoot 的实现：计算并返回 Path.GetFullPath(path) .StartsWith( Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.Dire…。
    private bool IsInsideRoot(string path) => Path.GetFullPath(path)
        .StartsWith(
            Paths.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        );
    public bool IsPaused
    {
        get
        {
            // [实现] 本段流程：锁定 sync 后访问共享状态。
            lock (sync)
                return paused;
        }
    }

    // [实现] SetPaused 的实现：锁定 sync 后访问共享状态。
    public void SetPaused(bool value)
    {
        lock (sync)
        {
            // [实现] 本段流程：执行 paused = value；执行 rateTicks = 0。
            paused = value;
            rateTicks = 0;
        }
    }

    // [实现] Configure 的实现：执行 options.Save(Paths)；锁定 sync 后访问共享状态。
    public void Configure(NetworkOptions options)
    {
        if (CanWrite?.Invoke() == false)
        {
            throw new InvalidOperationException("HOST_WRITER_ACTIVE");
        }

        options.Save(Paths);
        lock (sync)
        {
            // [实现] 本段流程：执行 Options = options；执行 rateTicks = 0。
            Options = options;
            rateTicks = 0;
        }
    }

    // [实现] Snapshot 的实现：锁定 sync 后访问共享状态。
    public IReadOnlyList<DownloadTaskRow> Snapshot()
    {
        lock (sync)
            return rows.Values.OrderBy(
                x => x.State is "下载中" or "排队中"
                    ? 0
                    : 1
            )
                .ThenByDescending(
                    x => x.Updated
                )
                .ToArray();
    }

    /// <summary>只筛选同哈希候选，不为每个下载排序整个任务列表；磁盘校验交给下载器在锁外完成。</summary>
    public IReadOnlyList<DownloadItem> ReusableCandidates(DownloadItem item)
    {
        lock (sync)
        {
            // [实现] 本段流程：返回 rows.Values.Select(x => x.Item).Where(x => x.Path != item.Path && x.Size == item.Size && x.Sha1…。
            return rows.Values.Select(
                x => x.Item
            )
                .Where(
                    x => x.Path != item.Path && x.Size == item.Size && x.Sha1 == item.Sha1
                )
                .ToArray();
        }
    }

    // [实现] Queue 的实现：准备 var id = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( Path.GetFullPath(item.Pa…；执行 Update( new(id, item, "排队中", 0, DateTimeOffset.UtcNow) )；返回 id。
    public string Queue(DownloadItem item)
    {
        var id = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    Path.GetFullPath(item.Path)
                )
            )
        );
        Update(
            new(id, item, "排队中", 0, DateTimeOffset.UtcNow)
        );
        return id;
    }

    // [实现] Report 的实现：执行 Update( new(id, item, state, bytes, DateTimeOffset.UtcNow, error) )。
    public void Report(string id, DownloadItem item, string state, long bytes, string? error = null) => Update(
        new(id, item, state, bytes, DateTimeOffset.UtcNow, error)
    );
    // [实现] Update 的实现：锁定 sync 后访问共享状态。
    private void Update(DownloadTaskRow row)
    {
        lock (sync)
        {
            // [实现] 本段流程：执行 rows[row.Id] = row；按 rows.Count > 1000 分支处理；按 DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(3) 分支处理。
            rows[row.Id] = row;
            if (rows.Count > 1000)
                foreach (var old in rows.Values.Where(
                    x => x.State == "已完成"
                )
                    .OrderBy(
                        x => x.Updated
                    )
                    .Take(rows.Count - 1000)
                    .ToArray())
                    rows.Remove(old.Id);
            if (DateTimeOffset.UtcNow - lastSaved > TimeSpan.FromSeconds(3))
                SaveLocked();
        }
    }

    // [实现] Flush 的实现：锁定 sync 后访问共享状态。
    public void Flush()
    {
        lock (sync)
            SaveLocked();
    }

    // [实现] SaveLocked 的实现：执行 JsonFiles.Write( file, rows.Values.ToArray() )；执行 lastSaved = DateTimeOffset.UtcNow。
    private void SaveLocked()
    {
        if (CanWrite?.Invoke() == false)
        {
            return;
        }

        JsonFiles.Write(
            file,
            rows.Values.ToArray()
        );
        lastSaved = DateTimeOffset.UtcNow;
    }

    // [实现] EnterAsync 的实现：在 true 条件下循环。
    public async Task EnterAsync(CancellationToken ct)
    {
        while (true)
        {
            // [实现] 循环满足 true 时：执行 ct.ThrowIfCancellationRequested()；锁定 sync 后访问共享状态；执行 await Task.Delay(100, ct)。
            ct.ThrowIfCancellationRequested();
            lock (sync)
                if (!paused && active < Options.ConcurrentFiles)
                {
                    // [实现] 条件成立（!paused && active < Options.ConcurrentFiles）时：执行 active++；结束当前过程。
                    active++;
                    return;
                }

            await Task.Delay(100, ct);
        }
    }

    // [实现] Leave 的实现：锁定 sync 后访问共享状态。
    public void Leave()
    {
        lock (sync)
            active--;
    }

    // [实现] FlowAsync 的实现：在 IsPaused 条件下循环；准备 TimeSpan wait；锁定 sync 后访问共享状态。
    public async Task FlowAsync(int bytes, CancellationToken ct)
    {
        while (IsPaused)
            await Task.Delay(100, ct);
        TimeSpan wait;
        lock (sync)
        {
            // [实现] 本段流程：按 Options.SpeedKiB == 0 分支处理；准备 var now = System.Diagnostics.Stopwatch.GetTimestamp()；准备 var start = Math.Max(now, rateTicks)。
            if (Options.SpeedKiB == 0)
                return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var start = Math.Max(now, rateTicks);
            rateTicks = start + (long)((double)bytes / (Options.SpeedKiB * 1024.0) * System.Diagnostics.Stopwatch.Frequency);
            wait = TimeSpan.FromSeconds((double)(start - now) / System.Diagnostics.Stopwatch.Frequency);
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }
}
