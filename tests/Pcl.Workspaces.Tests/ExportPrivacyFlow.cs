using System.IO.Compression;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

internal static class ExportPrivacyFlow
{
    // 只写合成隐私标记，走真实 Host 导出及分块取回；不读取真实凭据。
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
        }

        var game = Path.Combine(root, "instances", "privacy");
        void Seed(string name, string text)
        {
            var path = Path.Combine(game, name);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!
            );
            File.WriteAllText(path, text);
        }

        Seed("profile.json", "{\"id\":\"privacy\",\"gameVersion\":\"1.21.1\",\"loader\":\"vanilla\"}");
        Seed("version.json", "{\"id\":\"1.21.1\",\"libraries\":[]}");
        foreach (var name in new[]
        {
            "accounts.json",
            "launcher_accounts.json",
            "config/credentials.json",
            "config/nested/TOKENS.JSON",
            "config/auth.json",
            "config/session.lock",
            "config/servers.dat",
            "config/debug.log",
            "config/.env"
        }

        )
        {
            Seed(name, "BLOCKED-PRIVACY-CANARY");
        }

        Seed("config/keep.json", "{\"enabled\":true}");
        Seed("config/custom-service.json", "{\"apiKey\":\"UNKNOWN-CONFIG-CANARY\"}");
        Seed("resourcepacks/unused.txt", "UNSELECTED-CANARY");
        Seed("saves/Test/level.dat", "WORLD-OPT-IN-CANARY");
        Seed("options.txt", "OPTIONS-OPT-IN-CANARY");
        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var session = new LauncherSession(
            new HostClient(
                native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                native is null
                    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                    : "",
                root,
                reconnectable: true
            )
        );
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            foreach (var optIn in new[]
            {
                false,
                true
            }

            )
            {
                var archive = Path.Combine(
                    root,
                    optIn
                        ? "opt-in.mrpack"
                        : "default.mrpack"
                );
                await api.ExportAsync(
                    "workspace.packs.export",
                    "privacy",
                    new
                    {
                        options = new PackExportOptions(
                            "Privacy acceptance",
                            IncludeSaves: optIn,
                            IncludeOptions: optIn,
                            IncludedFolders: new[]
                            {
                                "config"
                            }
                        )
                    },
                    archive
                );
                using var zip = ZipFile.OpenRead(archive);
                var texts = new Dictionary<string, string>();
                foreach (var entry in zip.Entries)
                {
                    using var reader = new StreamReader(
                        entry.Open()
                    );
                    texts[entry.FullName] = await reader.ReadToEndAsync();
                }

                var all = string.Join("\n", texts.Values);
                Check(
                    !all.Contains("BLOCKED-PRIVACY-CANARY") && !all.Contains("UNSELECTED-CANARY"),
                    optIn
                        ? "PACK-011-opt-in-still-excludes-known-private-and-unselected-files"
                        : "PACK-011-default-excludes-known-private-and-unselected-files"
                );
                Check(
                    all.Contains("WORLD-OPT-IN-CANARY") == optIn && all.Contains("OPTIONS-OPT-IN-CANARY") == optIn,
                    optIn
                        ? "PACK-011-explicit-opt-in-includes-world-and-options"
                        : "PACK-011-world-and-options-excluded-by-default"
                );
                Check(
                    texts.ContainsKey("overrides/config/keep.json") && !all.Contains(root),
                    optIn
                        ? "PACK-011-opt-in-preserves-config-without-host-path"
                        : "PACK-011-default-preserves-config-without-host-path"
                );
                Check(
                    all.Contains("UNKNOWN-CONFIG-CANARY"),
                    "PACK-011-documented-limit-arbitrary-config-content-not-secret-scanned-" + optIn
                );
            }

            // 导出不得沿配置符号链接读取实例外文件，失败也不能交付半成品。
            var outside = Path.Combine(root, "outside-private.txt");
            await File.WriteAllTextAsync(outside, "OUTSIDE-PRIVATE-CANARY");
            var link = Path.Combine(game, "config", "outside-link.txt");
            File.CreateSymbolicLink(link, outside);
            var refused = false;
            var unsafeOutput = Path.Combine(root, "unsafe.mrpack");
            try
            {
                await api.ExportAsync(
                    "workspace.packs.export",
                    "privacy",
                    new
                    {
                        options = new PackExportOptions("Reject link")
                    },
                    unsafeOutput
                );
            }
            catch (Pcl.Core.Contracts.Operations.OperationRejectedException)
            {
                refused = true;
            }
            finally
            {
                File.Delete(link);
            }

            Check(
                refused && !File.Exists(unsafeOutput),
                "PACK-011-outside-symlink-rejected-without-delivery"
            );
            // 攻击性导入文件：即使外部文件主动携带命令与路径，也必须净化后写入。
            var hostile = Path.Combine(root, "hostile-settings.json");
            await File.WriteAllTextAsync(
                hostile,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        appearance = new AppearanceSettings
                        {
                            Title = "Portable acceptance",
                            Background = "/private/canary/background.png",
                            HomeDocument = "/private/canary/home.json",
                            MusicDirectory = "/private/canary/music"
                        },
                        launch = new LaunchSettings
                        {
                            JavaExecutable = "/private/canary/java",
                            JvmArguments = "-Dcanary=PRIVATE",
                            GameArguments = "--canary PRIVATE",
                            ServerAddress = "private.example.invalid",
                            QuickPlayWorld = "private-world"
                        }
                    }
                )
            );
            var restored = await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.import",
                null,
                new
                {
                    path = hostile
                }
            );
            var launch = restored.Launch.Deserialize<LaunchSettings>(
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }
            )!;
            Check(
                restored.Appearance.Background == "" && restored.Appearance.HomeDocument == "" && restored.Appearance.MusicDirectory == "",
                "UI-007-import-strips-local-media-and-home-paths"
            );
            Check(
                launch.JavaExecutable == "" && launch.JvmArguments == "" && launch.GameArguments == "" && launch.ServerAddress == "" && launch.QuickPlayWorld == "",
                "UI-007-import-strips-executable-arguments-server-and-world"
            );
            var export = Path.Combine(root, "portable-settings.json");
            await api.ExportAsync(
                "workspace.settings.export",
                null,
                new { },
                export
            );
            var contents = await File.ReadAllTextAsync(export);
            Check(
                !contents.Contains("/private/canary") && !contents.Contains("PRIVATE") && !contents.Contains("private.example.invalid") && contents.Contains("Portable acceptance"),
                "UI-007-export-contains-portable-values-only"
            );
            await session.ShutdownAsync();
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.ToString()
                    }
                )
            );
            await session.ShutdownAsync();
            throw;
        }
    }
}
