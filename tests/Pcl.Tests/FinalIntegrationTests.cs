using Pcl.Core;

internal static class FinalIntegrationTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool ok, string message)
        {
            if (!ok)
                throw new Exception(message);
        }

        await test(
            "metadata_transient_retry_and_invalid_json_preserve_previous_cache",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "metadata-retry")
                );
                var target = Path.Combine(paths.Root, "manifest.json");
                File.WriteAllText(target, "{\"old\":true}");
                var calls = 0;
                using (var service = new MinecraftService(
                    paths,
                    metadataHandler: new MetadataHandler(
                        (request, ct) =>
                        {
                            calls++;
                            if (calls == 1)
                                throw new HttpRequestException("connection reset");
                            if (calls == 2)
                            {
                                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                                return Task.FromResult(response);
                            }

                            return Task.FromResult(
                                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                                {
                                    Content = new StringContent("{\"fresh\":true}")
                                }
                            );
                        }
                    )
                ))
                {
                    var result = await service.GetMetadataAsync("https://example.invalid/manifest", target);
                    Check(calls == 3 && (bool? )result["fresh"] == true, "Transient request not recovered");
                }

                var previous = File.ReadAllText(target);
                calls = 0;
                using (var service = new MinecraftService(
                    paths,
                    metadataHandler: new MetadataHandler(
                        (request, ct) =>
                        {
                            calls++;
                            return Task.FromResult(
                                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                                {
                                    Content = new StringContent("broken JSON")
                                }
                            );
                        }
                    )
                ))
                {
                    try
                    {
                        await service.GetMetadataAsync("https://example.invalid/manifest", target);
                        throw new Exception("Invalid JSON accepted");
                    }
                    catch (Newtonsoft.Json.JsonReaderException)
                    {
                    }

                    Check(
                        calls == 1 && File.ReadAllText(target) == previous,
                        "Invalid JSON replaced previous metadata"
                    );
                }

                using (var cancel = new CancellationTokenSource())
                {
                    using var service = new MinecraftService(
                        paths,
                        metadataHandler: new MetadataHandler(
                            async (request, ct) =>
                            {
                                cancel.Cancel();
                                await Task.Delay(1000, ct);
                                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                            }
                        )
                    );
                    try
                    {
                        await service.GetMetadataAsync("https://example.invalid/manifest", target, cancellationToken: cancel.Token);
                        throw new Exception("Cancellation swallowed");
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    Check(
                        File.ReadAllText(target) == previous,
                        "Cancelled metadata replaced cache"
                    );
                }
            }
        );
        await test(
            "diagnostics_bounded_context_avoids_warning_and_platform_false_positive",
            () =>
            {
                Check(
                    DiagnosticsService.Analyze("[WARN] Warnings were found!\nOpenGL Error: 1282 (Invalid operation)")
                        .Count == 0,
                    "Normal warning falsely diagnosed"
                );
                Check(
                    !DiagnosticsService.Analyze("# C [ig9icd64.dll+0x1]\nOS: macOS")
                        .Any(
                            x => x.RuleId == "PCL-WIN-INTEL"
                        ),
                    "Mac diagnosed as Windows Intel driver"
                );
                Check(
                    DiagnosticsService.Analyze("EXCEPTION_ACCESS_VIOLATION\n# C [ig9icd64.dll+0x1]")
                        .Any(
                            x => x.RuleId == "PCL-WIN-INTEL"
                        ),
                    "Imported Windows fatal log not identified"
                );
                var manual = DiagnosticsService.Analyze("Manually triggered debug crash\njava.lang.RuntimeException\nOpenGL error 1282");
                Check(
                    manual.Count == 1 && manual[0].RuleId == "PCL-MANUAL-CRASH",
                    "Manual debug crash wrongly escalated"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "diagnostics_retains_dependency_config_world_context_and_redacts",
            () =>
            {
                var log = "Missing or unsupported mandatory dependencies:\nMod ID: example, Requested range: [2.0,), Actual version: 1.0\nFailed loading config file config/example.toml\naccessToken=private-token\nDescription: Ticking entity\njava.lang.RuntimeException: failed\nEntity Type: example:entity\nEntity's Exact location: 1, 2, 3";
                var result = DiagnosticsService.Analyze(log);
                Check(
                    result.Any(
                        x => x.RuleId == "PCL-MANDATORY-DEPENDENCY" && x.Evidence.Contains("[2.0,)")
                    ),
                    "Dependency context lost"
                );
                Check(
                    result.Any(
                        x => x.RuleId == "PCL-CONFIG"
                    ) && result.Any(
                        x => x.RuleId == "PCL-WORLD-ENTITY"
                    ),
                    "Structured diagnosis missing"
                );
                Check(
                    result.All(
                        x => !x.Evidence.Contains("private-token")
                    ),
                    "Diagnosis leaks credentials"
                );
                var stack = DiagnosticsService.Analyze(
                    "Fabric crash report\njava.lang.IllegalStateException: failed\n at dev.example.widget.Render.tick(Render.java:19)\n  example: Example Mod 1.0\nSystem Details"
                );
                Check(
                    stack.Any(
                        x => x.RuleId == "PCL-STACK-MOD" && x.Confidence.Contains("低置信度")
                    ),
                    "Stack inference lacks uncertainty"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "first_launch_chinese_preserves_existing_language_and_other_settings",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "language-settings")
                );
                var service = new AppearanceService(paths);
                var game = Path.Combine(paths.Root, "中文 游戏");
                Directory.CreateDirectory(game);
                service.PrepareGameLanguage(game, "1.21.1");
                Check(
                    File.ReadAllText(
                        Path.Combine(game, "options.txt")
                    )
                        .Contains("lang:zh_cn"),
                    "Modern language code wrong"
                );
                File.WriteAllText(
                    Path.Combine(game, "options.txt"),
                    "lang:en_us\nmusic:0.2\n"
                );
                service.PrepareGameLanguage(game, "1.12.2");
                Check(
                    File.ReadAllText(
                        Path.Combine(game, "options.txt")
                    ) == "lang:en_us\nmusic:0.2\n",
                    "Existing language replaced"
                );
                var old = Path.Combine(paths.Root, "legacy");
                service.PrepareGameLanguage(old, "1.10.2");
                Check(
                    File.ReadAllText(
                        Path.Combine(old, "options.txt")
                    )
                        .Contains("lang:zh_CN"),
                    "Old language code wrong"
                );
                service.Save(
                    new()
                    {
                        FirstLaunchChinese = false
                    }
                );
                var disabled = Path.Combine(paths.Root, "disabled");
                service.PrepareGameLanguage(disabled, "1.21.1");
                Check(
                    !Directory.Exists(disabled),
                    "Disabled option still modifies game"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "home_actions_reject_remote_commands_and_install_journal_retains_history",
            () =>
            {
                new HomeDocument(
                    "操作",
                    "",
                    [
                        new("实例", "instances", "navigate"),
                        new("复制", "literal text", "copy")
                    ]
                ).Validate();
                foreach (var link in new[]
                {
                    new HomeLink("执行", "/bin/sh", "command"),
                    new HomeLink("导航", "../private", "navigate")
                }

                )
                {
                    try
                    {
                        new HomeDocument(
                            "",
                            "",
                            [
                                link
                            ]
                        ).Validate();
                        throw new Exception("Unsafe action accepted");
                    }
                    catch (InvalidDataException)
                    {
                    }
                }

                var paths = new AppPaths(
                    Path.Combine(root, "journal")
                );
                var file = Path.Combine(paths.Root, "install-operations.json");
                JsonFiles.Write(
                    file,
                    new[]
                    {
                        new InstallOperation("active", "1.21.1", "fabric", "安装中", DateTimeOffset.UtcNow),
                        new InstallOperation("done", "1.20.1", "vanilla", "已完成", DateTimeOffset.UtcNow)
                    }
                );
                var journal = new InstallOperations(paths);
                journal.MarkInterrupted();
                var rows = journal.Read();
                Check(
                    rows.Single(
                        x => x.Id == "active"
                    )
                        .State == "已中断" && rows.Single(
                        x => x.Id == "done"
                    )
                        .State == "已完成",
                    "Restart lost completed task or left active stale"
                );
                journal.MarkInterrupted();
                Check(
                    journal.Read()
                        .Count == 2,
                    "Repeated recovery duplicated tasks"
                );
                return Task.CompletedTask;
            }
        );
    }
}

internal sealed class MetadataHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request, ct);
}
