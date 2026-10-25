using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pcl.Core;

internal static class WorkbenchTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool ok)
        {
            if (!ok)
                throw new Exception("Workbench assertion failed");
        }

        await test(
            "download_queue_pause_cancel_restart_and_verified_retry",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "download-queue")
                );
                var tasks = DownloadTasks.For(paths);
                tasks.SetPaused(true);
                var payload = Encoding.UTF8.GetBytes("integrity checked content");
                var handler = new ContentHandler(payload);
                using var downloads = new DownloadService(handler, paths);
                var file = new DownloadItem(
                    new("https://origin.test/file"),
                    Path.Combine(paths.Root, "file.bin"),
                    payload.Length,
                    Convert.ToHexString(
                        SHA1.HashData(payload)
                    )
                        .ToLowerInvariant()
                );
                using var cancel = new CancellationTokenSource(180);
                try
                {
                    await downloads.DownloadAsync(file, cancel.Token);
                    throw new Exception("Expected cancel");
                }
                catch (OperationCanceledException)
                {
                }

                Check(
                    handler.Calls == 0 && tasks.Snapshot()
                        .Single()
                        .State == "已取消"
                );
                tasks.SetPaused(false);
                await downloads.DownloadAsync(file);
                tasks.Flush();
                Check(
                    DownloadService.IsValid(file) && tasks.Snapshot()
                        .Single()
                        .State == "已完成" && File.Exists(
                        Path.Combine(paths.Root, "download-tasks.json")
                    )
                );
            }
        );
        await test(
            "download_partial_symlink_never_writes_outside_target",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "download-symlink")
                );
                var victim = Path.Combine(root, "untouched.txt");
                File.WriteAllText(victim, "preserve");
                var target = Path.Combine(paths.Root, "file.bin");
                File.CreateSymbolicLink(target + ".part", victim);
                using var downloads = new DownloadService(
                    new ContentHandler(
                        [
                            1,
                            2,
                            3
                        ]
                    ),
                    paths
                );
                try
                {
                    await downloads.DownloadAsync(
                        new(
                            new("https://origin.test/a"),
                            target,
                            3,
                            null
                        )
                    );
                    throw new Exception("Symlink accepted");
                }
                catch (InvalidDataException)
                {
                }

                Check(
                    File.ReadAllText(victim) == "preserve" && !File.Exists(target)
                );
            }
        );
        await test(
            "download_duplicate_target_serializes_across_service_instances",
            async () =>
            {
                var payload = new byte[32000];
                var handler = new ContentHandler(payload);
                using var a = new DownloadService(handler);
                using var b = new DownloadService(
                    new ContentHandler(payload)
                );
                var file = new DownloadItem(
                    new("https://origin.test/duplicate"),
                    Path.Combine(root, "same-target.bin"),
                    payload.Length,
                    Convert.ToHexString(
                        SHA1.HashData(payload)
                    )
                        .ToLowerInvariant()
                );
                await Task.WhenAll(
                    a.DownloadAsync(file),
                    b.DownloadAsync(file)
                );
                Check(
                    DownloadService.IsValid(file)
                );
            }
        );
        await test(
            "network_mirror_requires_original_digest_and_valid_proxy",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "network-mirror")
                );
                var options = new NetworkOptions(2, 0, "", "https://mirror.test", "origin.test", true);
                options.Save(paths);
                var payload = Encoding.UTF8.GetBytes("verified");
                var handler = new ContentHandler(payload, true);
                using var downloads = new DownloadService(handler, paths);
                var file = new DownloadItem(
                    new("https://origin.test/a/file"),
                    Path.Combine(paths.Root, "file.bin"),
                    payload.Length,
                    Convert.ToHexString(
                        SHA1.HashData(payload)
                    )
                        .ToLowerInvariant()
                );
                Check(
                    options.Sources(
                        file with
                        {
                            Sha1 = null
                        }
                    )
                        .Count == 1
                );
                await downloads.DownloadAsync(file);
                Check(
                    handler.Hosts.SequenceEqual(
                        new[]
                        {
                            "mirror.test",
                            "origin.test"
                        }
                    )
                );
                Check(
                    NetworkOptions.Load(paths)
                        .ConcurrentFiles == 2
                );
                try
                {
                    (options with
                    {
                        Proxy = "http://user:secret@proxy.test"
                    }

                    ).Validate();
                    throw new Exception("Must reject secret");
                }
                catch (InvalidDataException)
                {
                }
            }
        );
        await test(
            "diagnostic_analysis_and_export_redact_tokens_and_home",
            () =>
            {
                var text = "UnsupportedClassVersionError class file version 65\naccess_token=secret-token\n--accessToken another-secret\nBearer third-secret\n" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var redacted = DiagnosticsService.Redact(text);
                Check(
                    !redacted.Contains("secret") && !redacted.Contains(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    )
                );
                Check(
                    DiagnosticsService.Analyze(text)
                        .Any(
                            x => x.Cause.Contains("Java")
                        )
                );
                var log = Path.Combine(root, "diagnostic.log");
                File.WriteAllText(log, text);
                var zip = Path.Combine(root, "diagnostic.zip");
                DiagnosticsService.Export(log, zip);
                using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
                using var reader = new StreamReader(
                    archive.GetEntry("log-redacted.txt")!.Open()
                );
                Check(
                    !reader.ReadToEnd()
                        .Contains("secret")
                );
                return Task.CompletedTask;
            }
        );
    }

    private sealed class ContentHandler(byte[] content, bool mirrorFails = false) : HttpMessageHandler
    {
        public int Calls;
        public List<string> Hosts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Hosts.Add(request.RequestUri!.Host);
            await Task.Delay(30, ct);
            return request.RequestUri.Host == "mirror.test" && mirrorFails
                ? new(HttpStatusCode.ServiceUnavailable)
                : new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
        }
    }
}
