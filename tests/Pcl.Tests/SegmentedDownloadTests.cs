using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Pcl.Core;

internal static class SegmentedDownloadTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        var folder = Path.Combine(
            root,
            "segmented-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(folder);
        var data = new byte[SegmentedDownload.MinimumFileSize + 123];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)((i * 17L + i / 65536) % 251);
        var sha = Convert.ToHexString(
            SHA1.HashData(data)
        )
            .ToLowerInvariant();
        void Check(bool ok, string reason)
        {
            if (!ok)
                throw new Exception(reason);
        }

        AppPaths Paths(string name, bool enabled = true, int speed = 0)
        {
            var paths = new AppPaths(
                Path.Combine(folder, name)
            );
            new NetworkOptions(SpeedKiB: speed, SegmentedDownloads: enabled, SegmentWorkers: 3).Save(paths);
            return paths;
        }

        DownloadItem Item(AppPaths paths) => new(
            new Uri("https://fixture.invalid/official-client.jar"),
            Path.Combine(paths.Root, "client.jar"),
            data.Length,
            sha
        );
        await test(
            "segmented_legacy_network_settings_preserve_disabled_default",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(folder, "legacy-settings")
                );
                File.WriteAllText(
                    Path.Combine(paths.Root, "network.json"),
                    "{\"ConcurrentFiles\":3,\"SpeedKiB\":1024,\"Proxy\":\"\",\"MirrorBase\":\"\",\"MirrorHost\":\"\",\"PreferMirror\":false}"
                );
                var loaded = NetworkOptions.Load(paths);
                Check(
                    !loaded.SegmentedDownloads && loaded.SegmentWorkers == 4 && loaded.ConcurrentFiles == 3 && loaded.SpeedKiB == 1024,
                    "Old settings did not migrate safely"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "segmented_parallel_ranges_whole_hash_and_default_disabled",
            async () =>
            {
                Check(
                    !new NetworkOptions().SegmentedDownloads && !SegmentedDownload.Eligible(
                        new(
                            new Uri("https://example.com/a"),
                            "a",
                            data.Length,
                            null
                        )
                    ),
                    "Unsafe default/eligibility"
                );
                var paths = Paths("parallel", speed: 32768);
                var item = Item(paths);
                using var handler = new RangeHandler(data);
                using var downloads = new DownloadService(handler, paths);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await downloads.DownloadAsync(item);
                watch.Stop();
                Check(
                    DownloadService.IsValid(item) && downloads.LastSegmentedDownload is { Completed: true, Segments: 9, ReusedSegments: 0 },
                    "Segmented integrity or report wrong"
                );
                Check(
                    handler.MaxActive > 1 && handler.MaxActive <= 3 && handler.BlockStarts.Distinct()
                        .Count() == 9,
                    "Workers were not bounded and dynamically scheduled"
                );
                Check(
                    watch.Elapsed >= TimeSpan.FromMilliseconds(850),
                    "Shared aggregate speed limit not applied"
                );
                Check(
                    !Directory.Exists(item.Path + ".segments"),
                    "Completed block cache not cleaned"
                );
            }
        );
        await test(
            "segmented_no_range_and_wrong_content_range_fall_back",
            async () =>
            {
                foreach (var wrongBlock in new[]
                {
                    false,
                    true
                }

                )
                {
                    var paths = Paths(
                        wrongBlock
                            ? "bad-range"
                            : "no-range"
                    );
                    var item = Item(paths);
                    using var handler = new RangeHandler(data)
                    {
                        IgnoreRange = !wrongBlock,
                        WrongBlockRange = wrongBlock
                    };
                    using var downloads = new DownloadService(handler, paths);
                    await downloads.DownloadAsync(item);
                    Check(
                        DownloadService.IsValid(item) && downloads.LastSegmentedDownload is { Completed: false } && handler.WholeRequests == 1,
                        "Unsafe or absent sequential fallback"
                    );
                }
            }
        );
        await test(
            "segmented_cancellation_restart_reuses_complete_blocks",
            async () =>
            {
                var paths = Paths("restart");
                var item = Item(paths);
                File.WriteAllText(item.Path, "old target must survive cancellation");
                using var cancellation = new CancellationTokenSource();
                using (var first = new DownloadService(
                    new RangeHandler(data)
                    {
                        StopAfterOffset = SegmentedDownload.BlockSize * 3L,
                        Stop = cancellation
                    },
                    paths
                ))
                {
                    try
                    {
                        await first.DownloadAsync(item, cancellation.Token);
                        throw new Exception("Cancellation expected");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                Check(
                    File.ReadAllText(item.Path) == "old target must survive cancellation",
                    "Cancelled download replaced existing target"
                );
                var completed = Directory.GetFiles(item.Path + ".segments", "*.bin", SearchOption.AllDirectories);
                Check(completed.Length > 0 && completed.Length < 9, "No complete blocks persisted");
                using var handler = new RangeHandler(data);
                using var second = new DownloadService(handler, paths);
                await second.DownloadAsync(item);
                Check(
                    DownloadService.IsValid(item) && second.LastSegmentedDownload!.ReusedSegments == completed.Length,
                    "Restart did not reuse verified completed blocks"
                );
                Check(
                    handler.BlockStarts.Count == 9 - completed.Length,
                    "Restart downloaded completed blocks again"
                );
            }
        );
        await test(
            "segmented_tampered_chunk_rechecked_and_hash_failure_keeps_old_target",
            async () =>
            {
                var paths = Paths("tampered");
                var item = Item(paths);
                using var cancellation = new CancellationTokenSource();
                using (var first = new DownloadService(
                    new RangeHandler(data)
                    {
                        StopAfterOffset = SegmentedDownload.BlockSize * 3L,
                        Stop = cancellation
                    },
                    paths
                ))
                {
                    try
                    {
                        await first.DownloadAsync(item, cancellation.Token);
                        throw new Exception("Cancellation expected");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                var completed = Directory.GetFiles(item.Path + ".segments", "*.bin", SearchOption.AllDirectories);
                var victim = completed[0];
                using (var file = File.Open(victim, FileMode.Open, FileAccess.Write))
                    file.WriteByte((byte)(data[0] ^ 0xff));
                var index = int.Parse(
                    Path.GetFileNameWithoutExtension(victim)
                );
                using (var handler = new RangeHandler(data))
                    using (var second = new DownloadService(handler, paths))
                    {
                        await second.DownloadAsync(item);
                        Check(
                            DownloadService.IsValid(item) && handler.BlockStarts.Contains(index * (long)SegmentedDownload.BlockSize),
                            "Tampered cached block trusted"
                        );
                    }

                var failingPaths = Paths("bad-content");
                var bad = Item(failingPaths);
                File.WriteAllText(bad.Path, "untouched old target");
                using var invalid = new DownloadService(
                    new RangeHandler(data)
                    {
                        CorruptAll = true
                    },
                    failingPaths
                );
                try
                {
                    await invalid.DownloadAsync(bad);
                    throw new Exception("Hash failure expected");
                }
                catch (InvalidDataException)
                {
                }

                Check(
                    File.ReadAllText(bad.Path) == "untouched old target",
                    "Invalid content replaced old target"
                );
            }
        );
        await test(
            "segmented_rejects_dangling_cache_symlink",
            async () =>
            {
                if (OperatingSystem.IsWindows())
                    return;
                var paths = Paths("symlink");
                var item = Item(paths);
                Directory.CreateSymbolicLink(
                    item.Path + ".segments",
                    Path.Combine(paths.Root, "missing-outside")
                );
                using var handler = new RangeHandler(data);
                using var downloads = new DownloadService(handler, paths);
                try
                {
                    await downloads.DownloadAsync(item);
                    throw new Exception("Symlink rejection expected");
                }
                catch (InvalidDataException)
                {
                }

                Check(
                    handler.BlockStarts.IsEmpty && !File.Exists(item.Path),
                    "Symlink cache used"
                );
            }
        );
    }

    private sealed class RangeHandler(byte[] data) : HttpMessageHandler
    {
        public bool IgnoreRange;
        public bool WrongBlockRange;
        public bool CorruptAll;
        public long? StopAfterOffset;
        public CancellationTokenSource? Stop;
        public readonly ConcurrentBag<long> BlockStarts = new();
        public int WholeRequests;
        public int MaxActive;
        private int active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var ranges = request.Headers.Range?.Ranges.Single();
            var from = ranges?.From ?? 0;
            var to = ranges?.To ?? data.Length - 1;
            var probe = ranges is not null && from == 0 && to == 0;
            if (ranges is null)
                Interlocked.Increment(ref WholeRequests);
            if (ranges is not null && !probe)
            {
                if (StopAfterOffset is { } stop && from >= stop)
                {
                    Stop!.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                BlockStarts.Add(from);
            }

            var count = Interlocked.Increment(ref active);
            int previous;
            do
            {
                previous = MaxActive;
                if (count <= previous)
                    break;
            }
            while (Interlocked.CompareExchange(ref MaxActive, count, previous) != previous);
            try
            {
                await Task.Delay(12, ct);
                var whole = ranges is null || IgnoreRange;
                var bytes = whole
                    ? data.ToArray()
                    : data.AsSpan((int)from, (int)(to - from + 1))
                        .ToArray();
                if (CorruptAll)
                    bytes[0] ^= 0xff;
                var response = new HttpResponseMessage(
                    whole
                        ? HttpStatusCode.OK
                        : HttpStatusCode.PartialContent
                )
                {
                    Content = new ByteArrayContent(bytes)
                };
                if (!whole)
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                        from,
                        to,
                        data.Length + (WrongBlockRange && !probe
                            ? 1
                            : 0)
                    );
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture-v1\"");
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}
