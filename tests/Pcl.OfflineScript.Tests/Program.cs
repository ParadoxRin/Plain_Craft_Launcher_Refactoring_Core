using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

if (!OperatingSystem.IsMacOS())
{
    throw new PlatformNotSupportedException("Mac integration test");
}

var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
var checks = new List<string>();
void Check(bool ok, string name)
{
    if (!ok)
    {
        throw new Exception(name);
    }

    checks.Add(name);
    Console.WriteLine(name);
}

Directory.CreateDirectory(root);
try
{
    // 真正运行 /bin/sh，检查每个参数被原样传入，而不是只检查生成字符串。
    var folder = Path.Combine(root, "quote ' $literal");
    Directory.CreateDirectory(folder);
    var recorder = Path.Combine(folder, "java recorder");
    await File.WriteAllTextAsync(recorder, "#!/bin/sh\nprintf '%s\\0' \"$@\"\n");
    File.SetUnixFileMode(recorder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var values = new[]
    {
        "plain",
        "space ' \" quote",
        "$(touch SHOULD_NOT_EXIST)",
        "`echo nope`",
        "line\nnext",
        "",
        "a;b&c|d",
        "\\"
    };
    var identity = Pcl.Core.AccountManager.OfflineSession("PCL033");
    var script = Path.Combine(folder, "test.command");
    await File.WriteAllTextAsync(
        script,
        OfflineLaunchScript.Render(
            new(recorder, values, folder),
            identity
        )
    );
    var start = new ProcessStartInfo("/bin/sh")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add(script);
    using (var process = Process.Start(start)!)
    {
        var actual = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(
            process.ExitCode == 0 && actual.Split('\0')[..^1].SequenceEqual(values) && !File.Exists(
                Path.Combine(folder, "SHOULD_NOT_EXIST")
            ),
            "real-shell-preserves-special-characters-empty-and-newline-arguments"
        );
    }

    foreach (var invalid in new[]
    {
        new Pcl.Core.GameSession(
            "Fake",
            new string ('0', 32),
            "secret",
            "msa",
            false
        ),
        Pcl.Core.GameSession.Trial
    }

    )
    {
        var refused = false;
        try
        {
            OfflineLaunchScript.Render(
                new(
                    recorder,
                    [],
                    folder
                ),
                invalid
            );
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Check(refused, "non-offline-session-rejected-" + invalid.UserType);
    }

    var denied = false;
    try
    {
        OfflineLaunchScript.Render(
            new(
                recorder,
                [
                    "--accessToken",
                    "not-offline"
                ],
                folder
            ),
            identity
        );
    }
    catch (InvalidDataException)
    {
        denied = true;
    }

    Check(denied, "authentication-override-rejected-before-export");
    using (var service = new MinecraftService(
        new AppPaths(
            Path.Combine(root, "validation")
        )
    ))
    {
        var existing = Path.Combine(root, "existing.command");
        await File.WriteAllTextAsync(existing, "keep");
        try
        {
            await new InstanceManager(service).ExportOfflineScriptAsync(null!, existing);
        }
        catch (IOException)
        {
        }

        Check(
            await File.ReadAllTextAsync(existing) == "keep",
            "existing-destination-not-overwritten"
        );
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try
        {
            await new InstanceManager(service).ExportOfflineScriptAsync(
                null!,
                Path.Combine(root, "cancelled.command"),
                ct: cts.Token
            );
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check(
            cancelled && !File.Exists(
                Path.Combine(root, "cancelled.command")
            ),
            "cancel-before-preparation-leaves-no-script"
        );
    }

    var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
    await using (var session = new LauncherSession(
        new HostClient(
            native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
            native is null
                ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                : "",
            root,
            reconnectable: true
        )
    ))
    {
        await session.InitializeAsync();
        var api = new WorkspaceApi(session.Workspaces);
        var profile = session.DesktopState.Profiles.First(
            x => x.GameVersion == "1.21.1" && x.Loader == "vanilla"
        );
        var account = await api.CallAsync<Pcl.Core.Contracts.Workspaces.LauncherAccount>(
            "workspace.account.addOffline",
            null,
            new
            {
                name = "PCL033"
            }
        );
        await api.CallAsync<SkinState>(
            "workspace.account.skinPreset",
            null,
            new
            {
                accountId = account.Id,
                preset = "alex"
            }
        );
        var before = await File.ReadAllTextAsync(
            Path.Combine(root, "accounts.json")
        );
        var target = Path.Combine(root, "Minecraft standalone.command");
        await api.ExportAsync(
            "workspace.instances.offlineScript",
            profile.Id,
            new
            {
                playerName = "PCL033"
            },
            target
        );
        var content = await File.ReadAllTextAsync(target);
        Check(
            content.StartsWith("#!/bin/sh\n") && content.Contains("PCL033") && content.Contains("--accessToken") && !content.Contains("Pcl.Host") && !content.Contains("dotnet"),
            "host-exports-standalone-java-script-through-controlled-blob"
        );
        Check(
            (File.GetUnixFileMode(target) & UnixFileMode.UserExecute) != 0 && (File.GetUnixFileMode(target) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0,
            "export-published-executable-and-owner-only"
        );
        Check(
            await File.ReadAllTextAsync(
                Path.Combine(root, "accounts.json")
            ) == before,
            "export-does-not-replace-or-select-login-account"
        );
        var skinPack = Path.Combine(profile.GameDirectory, "resourcepacks", OfflineSkinService.PackName);
        Check(
            File.Exists(skinPack) && content.Contains(
                Pcl.Core.AccountManager.OfflineSession("PCL033", "slim")
                    .Uuid
            ),
            "script-prepares-existing-offline-skin-and-matching-slim-uuid"
        );
        var original = SHA256.HashData(
            await File.ReadAllBytesAsync(target)
        );
        var overwriteDenied = false;
        try
        {
            await api.ExportAsync(
                "workspace.instances.offlineScript",
                profile.Id,
                new
                {
                    playerName = "Other"
                },
                target
            );
        }
        catch (IOException)
        {
            overwriteDenied = true;
        }

        Check(
            overwriteDenied && SHA256.HashData(
                await File.ReadAllBytesAsync(target)
            )
                .SequenceEqual(original),
            "client-export-preserves-existing-script"
        );
        var invalidName = false;
        try
        {
            await api.ExportAsync(
                "workspace.instances.offlineScript",
                profile.Id,
                new
                {
                    playerName = "bad\nname"
                },
                Path.Combine(root, "bad.command")
            );
        }
        catch (OperationRejectedException)
        {
            invalidName = true;
        }

        Check(
            invalidName && !File.Exists(
                Path.Combine(root, "bad.command")
            ),
            "host-refuses-invalid-player-name-without-export"
        );
        await session.ShutdownAsync();
    }

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
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
    throw;
}
