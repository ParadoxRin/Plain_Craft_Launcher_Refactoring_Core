using System.Net;
using System.Security.Cryptography;
using System.Text;
using Pcl.Core;

// 检查设置的实际消费者：来源故障回退、缓存复制、脱敏诊断、取消、播放列表与实例继承。
internal static class LocalSettingsTests
{
    private static void Check(bool value, string message = "Local settings assertion failed")
    {
        if (!value)
        {
            throw new Exception(message);
        }
    }

    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        await test(
            "local_settings_metadata_fallback_invalid_json_and_priority",
            async () =>
            {
                var calls = new List<string>();
                using var client = new HttpClient(
                    new Handler(
                        request =>
                        {
                            calls.Add(request.RequestUri!.Host);
                            return new StringContent(
                                request.RequestUri.Host == "official.test"
                                    ? "<html>broken</html>"
                                    : "{\"ok\":true}"
                            );
                        }
                    )
                );
                var data = await MetadataSourcePolicy.ReadAsync(client, "https://official.test/a", "https://mirror.test/a", "fallback", default);
                Check(
                    data.Contains("true") && calls.SequenceEqual(
                        new[]
                        {
                            "official.test",
                            "mirror.test"
                        }
                    )
                );
                calls.Clear();
                await MetadataSourcePolicy.ReadAsync(client, "https://official.test/a", "https://mirror.test/a", "mirror", default);
                Check(
                    calls.SequenceEqual(
                        new[]
                        {
                            "mirror.test"
                        }
                    )
                );
            }
        );
        await test(
            "local_settings_copy_toggle_debug_redaction_and_delay_cancel",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "local-copy")
                );
                var bytes = Encoding.UTF8.GetBytes("verified cache content");
                var source = Path.Combine(paths.Root, "source.jar");
                File.WriteAllBytes(source, bytes);
                var item = new DownloadItem(
                    new Uri("https://files.test/file?token=secret-marker"),
                    source,
                    bytes.Length,
                    Convert.ToHexString(
                        SHA1.HashData(bytes)
                    )
                        .ToLowerInvariant()
                );
                var tasks = DownloadTasks.For(paths);
                tasks.Queue(item);
                var requests = 0;
                using var downloader = new DownloadService(
                    new Handler(
                        _ =>
                        {
                            requests++;
                            return new ByteArrayContent(bytes);
                        }
                    ),
                    paths
                );
                await downloader.DownloadAsync(
                    item with
                    {
                        Path = Path.Combine(paths.Root, "copy.jar")
                    }
                );
                Check(
                    requests == 0 && File.ReadAllBytes(
                        Path.Combine(paths.Root, "copy.jar")
                    )
                        .SequenceEqual(bytes)
                );
                tasks.Configure(
                    new NetworkOptions
                    {
                        DisableLocalCopy = true,
                        DebugMode = true
                    }
                );
                await downloader.DownloadAsync(
                    item with
                    {
                        Path = Path.Combine(paths.Root, "network.jar")
                    }
                );
                Check(requests == 1);
                var log = File.ReadAllText(
                    Path.Combine(paths.Evidence, "download-debug.log")
                );
                Check(
                    log.Contains("files.test") && !log.Contains("secret-marker") && !log.Contains("token=")
                );
                tasks.Configure(
                    new NetworkOptions
                    {
                        ArtificialDelayMs = 1000
                    }
                );
                using var cancel = new CancellationTokenSource(60);
                try
                {
                    await downloader.DownloadAsync(
                        item with
                        {
                            Path = Path.Combine(paths.Root, "cancel.jar")
                        },
                        cancel.Token
                    );
                    throw new Exception("Delay ignored cancellation");
                }
                catch (OperationCanceledException)
                {
                }

                Check(
                    !File.Exists(
                        Path.Combine(paths.Root, "cancel.jar")
                    ) && requests == 1
                );
            }
        );
        await test(
            "local_settings_window_size_and_instance_inheritance",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "window-local")
                );
                JsonFiles.Write(
                    Path.Combine(paths.Root, "launcher-window.json"),
                    new
                    {
                        width = 1001,
                        height = 701
                    }
                );
                var global = new LaunchSettings
                {
                    GameWindowTitle = "测试窗口",
                    WindowMode = "launcher",
                    ProcessPriority = "low"
                };
                var effective = new LaunchSettingsPatch
                {
                    GameWindowTitle = "实例窗口"
                }.Apply(global);
                Check(effective.GameWindowTitle == "实例窗口" && effective.ProcessPriority == "low");
                var size = MacWindowPolicy.ResolveSize(effective, paths);
                Check(size.Width == 1001 && size.Height == 701);
                Check(
                    MacWindowPolicy.ResolveSize(
                        global with
                        {
                            WindowMode = "default"
                        },
                        paths
                    )
                        .Width == 854
                );
                Check(
                    LaunchSettingsPatch.Parse(
                        LaunchSettingsPatch.From(effective)
                            .ToJson()
                    )
                        .Apply(
                            new()
                        ) == effective
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "local_settings_resource_filename_formats_and_path_safety",
            () =>
            {
                foreach (var style in new[]
                {
                    "original",
                    "brackets",
                    "square",
                    "prefix",
                    "suffix"
                }

                )
                {
                    var name = LocalResourcePreferences.FileName("mod-1.jar", "中文/模组", style);
                    Check(
                        name.EndsWith(".jar") && !name.Contains('/') && (style == "original" || name.Contains("中文_模组"))
                    );
                }

                try
                {
                    LocalResourcePreferences.FileName("../bad.jar", "name", "prefix");
                    throw new Exception("Traversal accepted");
                }
                catch (InvalidDataException)
                {
                }

                return Task.CompletedTask;
            }
        );
        await test(
            "local_settings_home_presets_blank_and_hidden_restore_model",
            async () =>
            {
                var service = new AppearanceService(
                    new AppPaths(
                        Path.Combine(root, "local-home")
                    )
                );
                foreach (var preset in new[]
                {
                    "welcome",
                    "shortcuts",
                    "tips"
                }

                )
                {
                    service.Save(
                        new AppearanceSettings
                        {
                            HomeMode = "preset",
                            HomePreset = preset,
                            HiddenRoutes = AppearanceFeatures.HiddenRoutes
                        }
                    );
                    Check(
                        (await service.ReadHomeAsync()).Body.Length > 0
                    );
                }

                service.Save(
                    new AppearanceSettings
                    {
                        HomeMode = "blank"
                    }
                );
                Check(
                    (await service.ReadHomeAsync()).Body.Length == 0
                );
            }
        );
        await test(
            "local_settings_named_resource_install_update_disable_remove",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "named-resource")
                );
                DownloadTasks.For(paths)
                    .Configure(
                        new NetworkOptions
                        {
                            FileNameStyle = "brackets",
                            ModNameStyle = "file"
                        }
                    );
                byte[] bytes;
                using (var memory = new MemoryStream())
                {
                    using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        using var writer = new StreamWriter(
                            zip.CreateEntry("fabric.mod.json")
                                .Open()
                        );
                        writer.Write("{\"id\":\"fixture\",\"version\":\"1\"}");
                    }

                    bytes = memory.ToArray();
                }

                using var downloads = new DownloadService(
                    new Handler(
                        _ => new ByteArrayContent(bytes)
                    ),
                    paths
                );
                var store = new ResourceInstaller(paths.Game, downloads);
                var release = new ResourceRelease(
                    "v1",
                    "fixture",
                    "测试模组",
                    "1",
                    "release",
                    DateTimeOffset.UtcNow,
                    [
                        "1.21.1"
                    ],
                    [
                        "fabric"
                    ],
                    new ResourceFile(
                        "fixture.jar",
                        new Uri("https://file.test/a"),
                        bytes.Length,
                        Convert.ToHexString(
                            SHA1.HashData(bytes)
                        )
                            .ToLowerInvariant()
                    ),
                    []
                );
                await store.InstallAsync(
                    new(
                        ResourceKind.Mod,
                        "fixture",
                        [
                            release
                        ]
                    )
                );
                var installed = store.List()
                    .Single();
                Check(
                    installed.FileName == "【测试模组】fixture.jar" && File.Exists(
                        Path.Combine(paths.Game, "mods", installed.FileName)
                    )
                );
                Check(
                    LocalResourcePreferences.Display(installed, "file")
                        .StartsWith(installed.FileName)
                );
                store.SetEnabled("fixture", false);
                Check(
                    File.Exists(
                        Path.Combine(paths.Game, "mods", installed.FileName + ".disabled")
                    )
                );
                store.SetEnabled("fixture", true);
                var updated = release with
                {
                    Id = "v2",
                    Number = "2",
                    Name = "测试新版"
                };
                await store.InstallAsync(
                    new(
                        ResourceKind.Mod,
                        "fixture",
                        [
                            updated
                        ]
                    )
                );
                Check(
                    !File.Exists(
                        Path.Combine(paths.Game, "mods", installed.FileName)
                    ) && File.Exists(
                        Path.Combine(paths.Game, "mods", "【测试新版】fixture.jar")
                    )
                );
                store.Remove("fixture");
                Check(
                    store.List()
                        .Count == 0 && !File.Exists(
                        Path.Combine(paths.Game, "mods", "【测试新版】fixture.jar")
                    )
                );
            }
        );
        await test(
            "local_settings_unsafe_agent_compatibility_and_explicit_disable",
            () =>
            {
                var meta = Newtonsoft.Json.Linq.JObject.Parse("{\"libraries\":[{\"name\":\"org.lwjgl:lwjgl:3.4.1\"}]}");
                Check(
                    LwjglUnsafePolicy.Applies(
                        new(),
                        25,
                        meta
                    )
                );
                Check(
                    !LwjglUnsafePolicy.Applies(
                        new(),
                        21,
                        meta
                    )
                );
                Check(
                    LwjglUnsafePolicy.Arguments(
                        new()
                        {
                            DisableLwjglUnsafeAgent = true
                        },
                        25,
                        meta
                    )
                        .Length == 0
                );
                meta["libraries"]![0]!["name"] = "org.lwjgl:lwjgl:3.4.3";
                Check(
                    !LwjglUnsafePolicy.Applies(
                        new(),
                        25,
                        meta
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "local_settings_random_background_and_portable_path_stripping",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "local-background")
                );
                var directory = Path.Combine(paths.Root, "backgrounds");
                Directory.CreateDirectory(directory);
                var one = Path.Combine(directory, "a.png");
                var two = Path.Combine(directory, "b.png");
                File.WriteAllText(one, "selection fixture");
                File.WriteAllText(two, "selection fixture");
                var settings = new AppearanceSettings
                {
                    BackgroundDirectory = directory,
                    MusicDirectory = directory,
                    TitleImage = one
                };
                Check(
                    AppearanceFeatures.SelectBackground(settings, one) == two
                );
                File.Delete(two);
                Check(
                    AppearanceFeatures.SelectBackground(settings, one) == one
                );
                var service = new AppearanceService(paths);
                service.Save(settings);
                var export = Path.Combine(paths.Root, "portable.json");
                service.Export(export);
                Check(
                    !File.ReadAllText(export)
                        .Contains(directory)
                );
                service.Import(export);
                Check(
                    service.Load()
                        .BackgroundDirectory == "" && service.Load()
                        .MusicDirectory == "" && service.Load()
                        .TitleImage == ""
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "local_settings_actual_afplay_playlist_stop_and_restart",
            async () =>
            {
                if (!OperatingSystem.IsMacOS())
                {
                    return;
                }

                var folder = Path.Combine(root, "audio-playlist");
                Directory.CreateDirectory(folder);
                foreach (var name in new[]
                {
                    "a.wav",
                    "b.wav"
                }

                )
                {
                    using var stream = File.Create(
                        Path.Combine(folder, name)
                    );
                    using var writer = new BinaryWriter(stream);
                    var data = new byte[8000];
                    writer.Write(
                        Encoding.ASCII.GetBytes("RIFF")
                    );
                    writer.Write(36 + data.Length);
                    writer.Write(
                        Encoding.ASCII.GetBytes("WAVEfmt ")
                    );
                    writer.Write(16);
                    writer.Write((short)1);
                    writer.Write((short)1);
                    writer.Write(8000);
                    writer.Write(16000);
                    writer.Write((short)2);
                    writer.Write((short)16);
                    writer.Write(
                        Encoding.ASCII.GetBytes("data")
                    );
                    writer.Write(data.Length);
                    writer.Write(data);
                }

                var settings = new AppearanceSettings
                {
                    MusicDirectory = folder,
                    MusicShuffle = true,
                    MusicVolume = 0
                };
                using var player = new LocalMusicPlayer();
                player.Play(settings);
                for (var i = 0; i < 80 && player.TracksStarted < 2 && player.LastError is null; i++)
                {
                    await Task.Delay(100);
                }

                Check(
                    player.TracksStarted >= 2 && player.LastError is null,
                    player.LastError ?? "No playlist advancement"
                );
                player.Stop();
                var count = player.TracksStarted;
                await Task.Delay(800);
                Check(!player.IsPlaying && player.TracksStarted == count, "Stopped playlist restarted");
                player.Play(settings);
                await Task.Delay(300);
                Check(player.TracksStarted > count);
                player.Stop();
                Check(
                    AppearanceFeatures.ShouldPlayMusic(
                        settings with
                        {
                            MusicMode = "game"
                        },
                        true
                    ) && !AppearanceFeatures.ShouldPlayMusic(
                        settings with
                        {
                            MusicMode = "game"
                        },
                        false
                    )
                );
            }
        );
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpContent> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = response(request)
                }
            );
        }
    }
}
