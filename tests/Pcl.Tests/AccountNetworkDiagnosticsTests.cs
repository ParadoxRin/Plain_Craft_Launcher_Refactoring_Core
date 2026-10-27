using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class AccountNetworkDiagnosticsTests
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> test)
    {
        await test(
            "world_entry_failure_survives_clean_exit_and_has_specific_advice",
            async () =>
            {
                var text = "xaero.hud.minimap: java.lang.NullPointerException MinecraftServerData.getLevelProperties serverData is null\nError loading server minimap properties";
                if (!DiagnosticsService.Analyze(text)
                    .Any(
                        row => row.RuleId == "MAC-XAERO-WORLD-ENTRY"
                    ) || DiagnosticsService.HasWorldEntryFailure("java.lang.NullPointerException unrelated") || DiagnosticsService.HasWorldEntryFailure("xaero loaded successfully"))
                {
                    throw new Exception("World failure detection is missing or too broad.");
                }

                var log = Path.Combine(root, "world-exit-status.log");
                File.WriteAllText(log, text);
                if (!(await DiagnosticsService.GameExitStatusAsync(0, false, log)).Contains("进入世界失败"))
                {
                    throw new Exception("Exit zero hid the recorded world failure.");
                }

                File.WriteAllText(log, "normal game shutdown");
                if (await DiagnosticsService.GameExitStatusAsync(0, false, log) != "游戏已正常退出")
                {
                    throw new Exception("Clean game exit was incorrectly diagnosed.");
                }

                var forge = "net.minecraftforge.common.ForgeInternalHandler: Can not retrieve LootModifierManager until resources have loaded once.";
                if (!DiagnosticsService.HasWorldEntryFailure(forge) || !DiagnosticsService.Analyze(forge)
                    .Any(
                        row => row.RuleId == "MAC-FORGE-WORLD-INIT"
                    ))
                {
                    throw new Exception("Forge initialization failure missing from diagnostics.");
                }
            }
        );
        static void Check(bool value, string message)
        {
            if (!value)
                throw new Exception(message);
        }

        static async Task Throws<T>(Func<Task> work)
        where T : Exception
        {
            try
            {
                await work();
            }
            catch (T)
            {
                return;
            }

            throw new Exception("Expected " + typeof(T).Name);
        }

        await test(
            "network_probe_caps_ignored_range_and_never_queues_install",
            async () =>
            {
                var stream = new CountingStream(1024 * 1024);
                using var probe = new NetworkDiagnosticsService(
                    new(),
                    new Handler(
                        request =>
                        {
                            Check(
                                request.Headers.Range?.Ranges.Single()
                                    .From == 0 && request.Headers.Range.Ranges.Single()
                                    .To == 65535,
                                "bounded range request"
                            );
                            return Task.FromResult(
                                new HttpResponseMessage(HttpStatusCode.OK)
                                {
                                    Content = new StreamContent(stream)
                                }
                            );
                        }
                    )
                );
                var result = await probe.ProbeAsync(
                    new("Fixture", "https://example.test/sample.jar")
                );
                Check(
                    result.Success && result.Bytes == 65536 && stream.ReadCount == 65536 && !result.RangeHonored,
                    "stop reading after cap when server ignores Range"
                );
                await Throws<InvalidDataException>(
                    () => probe.ProbeAsync(
                        new("Unsafe", "https://example.test/file?token=secret")
                    )
                );
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Throws<OperationCanceledException>(
                    () => probe.ProbeAsync(
                        new("Fixture", "https://example.test/file"),
                        canceled.Token
                    )
                );
            }
        );
        await test(
            "network_probe_reports_redirect_and_bad_range_without_reading_body",
            async () =>
            {
                var stream = new CountingStream(1024);
                using var probe = new NetworkDiagnosticsService(
                    new(),
                    new Handler(
                        _ =>
                        {
                            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                            {
                                Content = new StreamContent(stream)
                            };
                            response.Content.Headers.ContentRange = new(10, 100, 1024);
                            return Task.FromResult(response);
                        }
                    )
                );
                var bad = await probe.ProbeAsync(
                    new("Wrong range", "https://example.test/file")
                );
                Check(!bad.Success && stream.ReadCount == 0, "unexpected range rejected before body");
                using var redirect = new NetworkDiagnosticsService(
                    new(),
                    new Handler(
                        _ => Task.FromResult(
                            new HttpResponseMessage(HttpStatusCode.Found)
                            {
                                Headers =
                                {
                                    Location = new Uri("http://unsafe.example.test/file")
                                }
                            }
                        )
                    )
                );
                var result = await redirect.ProbeAsync(
                    new("Redirect", "https://example.test/file")
                );
                Check(!result.Success && result.StatusCode == 302, "redirect not followed");
            }
        );
        var paths = new AppPaths(
            Path.Combine(
                root,
                "temporary-cache-" + Guid.NewGuid()
                    .ToString("N")
            )
        );
        var cache = new TemporaryDownloadCache(paths);
        var tasks = DownloadTasks.For(paths);
        string FileAt(string relative, string content, bool old = true)
        {
            var path = Path.Combine(paths.Root, relative);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!
            );
            File.WriteAllText(path, content);
            if (old)
                File.SetLastWriteTimeUtc(
                    path,
                    DateTime.UtcNow.AddDays(-3)
                );
            return path;
        }

        await test(
            "temporary_cache_excludes_active_recent_shared_and_symlink_files",
            () =>
            {
                var candidate = FileAt("cache/a.jar.part", "old");
                FileAt("cache/recent.part", "recent", false);
                FileAt("cache/verified.jar", "verified");
                FileAt("runtime/java.part", "java");
                FileAt("minecraft/libraries/a.jar.part", "lib");
                FileAt("minecraft/assets/a.part", "asset");
                var active = FileAt("instances/test/.pcl-downloads/active.part", "active");
                tasks.Queue(
                    new(
                        new("https://example.test/active"),
                        active[..^5],
                        10,
                        null
                    )
                );
                var external = FileAt("outside.part", "keep");
                File.CreateSymbolicLink(
                    Path.Combine(paths.Root, "cache", "link.part"),
                    external
                );
                var rows = cache.Preview();
                Check(
                    rows.Count == 1 && rows.Single()
                        .RelativePath == Path.Combine("cache", "a.jar.part"),
                    "only old idle explicit cache selected"
                );
                Check(
                    File.ReadAllText(candidate) == "old",
                    "preview does not mutate"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "temporary_cache_rechecks_preview_and_restores_without_overwrite",
            async () =>
            {
                var old = cache.Preview()
                    .Single();
                File.AppendAllText(
                    Path.Combine(paths.Root, old.RelativePath),
                    "changed"
                );
                var stale = cache.MoveToTrash(
                    [
                        old
                    ]
                );
                Check(stale.Batch is null && stale.Skipped.Count == 1, "stale preview not moved");
                var original = FileAt("cache/a.jar.part", "old");
                var selected = cache.Preview()
                    .Single();
                var moved = cache.MoveToTrash(
                    [
                        selected
                    ]
                );
                Check(
                    moved.Batch?.Files.Count == 1 && !File.Exists(original),
                    "file moved recoverably"
                );
                File.WriteAllText(original, "new download");
                var blocked = cache.Restore(moved.Batch!.Id);
                Check(
                    blocked.Batch?.Files.Count == 1 && File.ReadAllText(original) == "new download",
                    "restore does not overwrite new partial"
                );
                File.Delete(original);
                cache.Restore(moved.Batch.Id);
                Check(
                    File.ReadAllText(original) == "old" && cache.ListTrash()
                        .Count == 0,
                    "exact partial restored"
                );
                await Throws<InvalidDataException>(
                    () => Task.Run(
                        () => cache.MoveToTrash(
                            [
                                new(
                                    "minecraft/libraries/a.jar.part",
                                    3,
                                    DateTimeOffset.UtcNow.AddDays(-3)
                                )
                            ]
                        )
                    )
                );
            }
        );
        // 使用真实句柄验证占用保护；释放后同一批文件必须能完成回收和恢复。
        await test(
            "temporary_cache_occupied_move_and_restore_retain_bytes_and_receipt",
            () =>
            {
                var isolated = new AppPaths(Path.Combine(root, "cache-handle-protection"));
                var isolatedCache = new TemporaryDownloadCache(isolated);
                var original = Path.Combine(isolated.Root, "cache", "locked.part");
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.WriteAllText(original, "locked bytes");
                File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-3));
                var selected = isolatedCache.Preview().Single();
                using (var writer = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var blocked = isolatedCache.MoveToTrash([selected]);
                    Check(blocked.Batch is null && blocked.Skipped.Count == 1
                        && File.Exists(original) && isolatedCache.ListTrash().Count == 0,
                        "occupied source stays outside trash");
                }
                Check(File.ReadAllText(original) == "locked bytes", "blocked move preserves bytes");
                var moved = isolatedCache.MoveToTrash([selected]);
                Check(moved.Batch?.Files.Count == 1 && moved.Skipped.Count == 0
                    && !File.Exists(original), "released source moves with validation handle open");
                var batch = moved.Batch!;
                var stored = Path.Combine(isolated.Root, "cache-trash", batch.Id, batch.Files.Single().StoredName);
                using (var writer = new FileStream(stored, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var blocked = isolatedCache.Restore(batch.Id);
                    Check(blocked.Batch?.Files.Count == 1 && blocked.Skipped.Count == 1
                        && File.Exists(stored) && !File.Exists(original)
                        && isolatedCache.ListTrash().Single().Id == batch.Id,
                        "occupied trash retains receipt and does not create destination");
                }
                Check(File.ReadAllText(stored) == "locked bytes", "blocked restore preserves bytes");
                var restored = isolatedCache.Restore(batch.Id);
                Check(restored.Skipped.Count == 0 && File.ReadAllText(original) == "locked bytes"
                    && !File.Exists(stored) && isolatedCache.ListTrash().Count == 0,
                    "released trash restores exact bytes and removes receipt");
                return Task.CompletedTask;
            }
        );
        // 预览之后才排队的下载仍受保护，不能依赖预览时的空闲状态。
        await test(
            "temporary_cache_rechecks_download_queued_after_preview",
            () =>
            {
                var isolated = new AppPaths(Path.Combine(root, "cache-late-download"));
                var isolatedCache = new TemporaryDownloadCache(isolated);
                var original = Path.Combine(isolated.Root, "cache", "active.part");
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                File.WriteAllText(original, "active bytes");
                File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-3));
                var selected = isolatedCache.Preview().Single();
                DownloadTasks.For(isolated).Queue(new(new("https://example.test/late"), original[..^5], 12, null));
                var result = isolatedCache.MoveToTrash([selected]);
                Check(result.Batch is null && result.Skipped.Count == 1
                    && File.ReadAllText(original) == "active bytes" && isolatedCache.ListTrash().Count == 0,
                    "newly queued download cannot be moved using stale preview");
                return Task.CompletedTask;
            }
        );
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return action(request);
        }
    }

    private sealed class CountingStream(int length) : Stream
    {
        private long position;
        public long ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - position);
            Array.Fill(buffer, (byte)1, offset, read);
            position += read;
            ReadCount += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - position);
            buffer.Span[..read].Fill(1);
            position += read;
            ReadCount += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
