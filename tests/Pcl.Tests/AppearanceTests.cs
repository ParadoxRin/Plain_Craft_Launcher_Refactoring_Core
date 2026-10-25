using Pcl.Core;

internal static class AppearanceTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value)
        {
            if (!value)
                throw new Exception("Appearance assertion failed");
        }

        await test(
            "settings_export_strips_executable_and_import_preserves_account_store",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "appearance")
                );
                var appearance = new AppearanceService(paths);
                var settings = new LauncherSettingsStore(paths);
                settings.SaveGlobal(
                    new()
                    {
                        MaximumMemoryMb = 4096,
                        JvmArguments = "-javaagent:/untrusted.jar",
                        ServerAddress = "private.example:1234"
                    }
                );
                appearance.Save(
                    new()
                    {
                        Theme = "dark",
                        HomeDocument = "https://example.com/home"
                    }
                );
                File.WriteAllText(
                    Path.Combine(paths.Root, "accounts.json"),
                    "private-marker"
                );
                var export = Path.Combine(root, "settings-export.json");
                appearance.Export(export);
                var json = JsonFiles.Read(export);
                Check(
                    !json.ToString()
                        .Contains("untrusted") && !json.ToString()
                        .Contains("private.example") && !json.ToString()
                        .Contains("private-marker")
                );
                json["launch"]!["JavaExecutable"] = "/malicious/executable";
                json["launch"]!["JvmArguments"] = "-javaagent:/malicious.jar";
                json["appearance"]!["Music"] = "/malicious/file";
                JsonFiles.Write(export, json);
                appearance.Import(export);
                Check(
                    settings.Global.JavaExecutable == "" && settings.Global.JvmArguments == "" && appearance.Load()
                        .Music == "" && settings.Global.MaximumMemoryMb == 4096
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(paths.Root, "accounts.json")
                    ) == "private-marker" && Directory.EnumerateFiles(
                        Path.Combine(paths.Root, "settings-backups"),
                        "launch-settings.json",
                        SearchOption.AllDirectories
                    )
                        .Count() == 1
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "custom_home_rejects_script_links_and_oversized_document",
            async () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "safe-home")
                );
                var service = new AppearanceService(paths);
                var file = Path.Combine(paths.Root, "home.json");
                service.Save(
                    new()
                    {
                        HomeDocument = file
                    }
                );
                JsonFiles.Write(
                    file,
                    new HomeDocument(
                        "Title",
                        "Body",
                        [
                            new("Unsafe", "javascript:alert(1)")
                        ]
                    )
                );
                try
                {
                    await service.ReadHomeAsync();
                    throw new Exception("Script link accepted");
                }
                catch (InvalidDataException)
                {
                }

                JsonFiles.Write(
                    file,
                    new HomeDocument(
                        "Title",
                        "Body",
                        [
                            new("Official", "https://minecraft.net")
                        ]
                    )
                );
                Check(
                    (await service.ReadHomeAsync()).Links.Length == 1
                );
                File.WriteAllText(
                    file,
                    new string ('a', 1_048_577)
                );
                try
                {
                    await service.ReadHomeAsync();
                    throw new Exception("Oversize accepted");
                }
                catch (InvalidDataException)
                {
                }
            }
        );
        await test(
            "launch_hook_literal_args_failure_and_timeout",
            async () =>
            {
                if (!OperatingSystem.IsMacOS())
                    return;
                using var runner = new LaunchHookRunner();
                var lines = new List<string>();
                await runner.RunAsync(
                    new(true, "/bin/echo", "'$(untrusted-command)'", true, 3),
                    root,
                    lines.Add
                );
                Check(
                    lines.Contains("$(untrusted-command)")
                );
                try
                {
                    await runner.RunAsync(
                        new(true, "/usr/bin/false", "", true, 3),
                        root
                    );
                    throw new Exception("Failure ignored");
                }
                catch (IOException)
                {
                }

                try
                {
                    await runner.RunAsync(
                        new(true, "/bin/sleep", "10", true, 1),
                        root
                    );
                    throw new Exception("Timeout ignored");
                }
                catch (TimeoutException)
                {
                }
            }
        );
        await test(
            "easytier_room_validation_and_no_shell_port_forward",
            () =>
            {
                var room = LinkRoom.Create(
                    25565,
                    [
                        "tcp://127.0.0.1:11010"
                    ]
                );
                var copy = LinkRoom.Decode(
                    room.Encode()
                );
                Check(copy.Name == room.Name && copy.Secret == room.Secret);
                var args = EasyTierService.Arguments(copy, false, 11011, 15888, 25566);
                Check(
                    args.Contains("--no-tun") && args.Contains("tcp://127.0.0.1:25566/10.114.114.114:25565") && !args.Contains("--enable-exit-node")
                );
                try
                {
                    (room with
                    {
                        Peers = [
                            "tcp://user:secret@example.com:11010"
                        ]
                    }

                    ).Validate();
                    throw new Exception("Credentials accepted");
                }
                catch (InvalidDataException)
                {
                }

                try
                {
                    LinkRoom.Decode("PCLMAC1:???");
                    throw new Exception("Invalid code accepted");
                }
                catch (InvalidDataException)
                {
                }

                return Task.CompletedTask;
            }
        );
    }
}
