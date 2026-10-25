using System.Text.Json;
using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Isolated end-to-end probe uses actual controls and Host, not a replacement UI service.
    private async Task ShellBoundarySmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--shell-boundary-report") + 1];
        var checks = new List<string>();
        TaskCenterWindow? tasks = null;
        void Check(bool ok, string name)
        {
            if (!ok)
                throw new Exception(name);
            checks.Add(name);
            File.AppendAllText(report + ".progress", name + "\n");
        }

        async Task Wait(Func<bool> pending)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (pending())
            {
                if (DateTime.UtcNow > until)
                    throw new TimeoutException("UI wait");
                await Task.Delay(80);
            }
        }

        T Named<T>(Window w, string name)
        where T : Control => w.GetVisualDescendants()
            .OfType<T>()
            .Single(
                x => x.Name == name
            );
        async Task Click(Window w, string text)
        {
            var b = w.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == text
                );
            Check(b.IsEffectivelyEnabled, "enabled-" + text);
            b.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Task.Delay(200);
        }

        async Task Capture(Window w, string name)
        {
            await Task.Delay(120);
            using var b = new RenderTargetBitmap(
                new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height),
                new Vector(96, 96)
            );
            b.Render(w);
            b.Save(
                Path.Combine(
                    Path.GetDirectoryName(report)!,
                    name + ".png"
                ),
                PngBitmapEncoderOptions.Default
            );
        }

        try
        {
            if (!paths.Root.Contains("architecture-0.32", StringComparison.Ordinal) || accounts!.Accounts.Any(
                a => a.Kind != AccountKind.Offline
            ))
                throw new Exception("ISOLATED_ROOT_REQUIRED");
            Check(
                !typeof(MainWindow).Assembly.GetReferencedAssemblies()
                    .Any(
                        a => a.Name is "Pcl.Core" or "Pcl.Application" or "MeloongCore"
                    ),
                "desktop-runtime-assembly-has-no-core-application-or-meloong"
            );
            await DesktopApi.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Network = new NetworkOptions
                        {
                            ConcurrentFiles = 3
                        }
                    }
                }
            );
            await File.WriteAllTextAsync(
                Path.Combine(paths.Evidence, "ui-diagnostic.log"),
                "java.lang.OutOfMemoryError\naccess_token=ui-synthetic-secret\n"
            );
            tasks = new(service, RunAsync, Open, operations: installation.Operations);
            tasks.Show(this);
            await Task.Delay(250);
            var tabs = tasks.GetVisualDescendants()
                .OfType<TabControl>()
                .Single();
            tabs.SelectedIndex = 3;
            await Task.Delay(100);
            await Wait(
                () => Named<TextBox>(tasks, "NetworkParallel")
                    .Text != "3"
            );
            Check(true, "network-tab-loads-authoritative-host-value");
            Named<TextBox>(tasks, "NetworkParallel")
                .Text = "4";
            await Click(tasks, "保存网络设置");
            var saveDeadline = DateTime.UtcNow.AddSeconds(30);
            while ((await service.Client.Call<DownloadSnapshot>(
                "downloads.snapshot",
                new { }
            )).Options.ConcurrentFiles != 4)
            {
                if (DateTime.UtcNow > saveDeadline)
                    throw new TimeoutException("Host network save");
                await Task.Delay(80);
            }

            Check(true, "network-save-button-persists-in-host");
            await Capture(tasks, "network");
            tabs.SelectedIndex = 2;
            await Task.Delay(100);
            await Click(tasks, "暂停下载");
            File.AppendAllText(report + ".progress", "pause-click-returned\n");
            Check(
                (await service.Client.Call<DownloadSnapshot>(
                    "downloads.snapshot",
                    new { }
                )
                    .WaitAsync(
                        TimeSpan.FromSeconds(15)
                    )).IsPaused,
                "download-pause-button-controls-host"
            );
            await Wait(
                () => !tasks.GetVisualDescendants()
                    .OfType<Button>()
                    .Any(
                        b => b.Content?.ToString() == "继续下载"
                    )
            );
            await Click(tasks, "继续下载");
            Check(
                !(await service.Client.Call<DownloadSnapshot>(
                    "downloads.snapshot",
                    new { }
                )
                    .WaitAsync(
                        TimeSpan.FromSeconds(15)
                    )).IsPaused,
                "download-resume-button-controls-host"
            );
            tabs.SelectedIndex = 4;
            await Task.Delay(200);
            await Wait(
                () => string.IsNullOrEmpty(
                    Named<TextBox>(tasks, "DiagnosticAnalysis")
                        .Text
                )
            );
            Check(
                !Named<TextBox>(tasks, "DiagnosticAnalysis")
                    .Text!.Contains("ui-synthetic-secret"),
                "diagnostic-tab-shows-redacted-host-analysis"
            );
            await Capture(tasks, "diagnostics");
            tasks.Close();
            await Task.Delay(200);
            tasks = new(service, RunAsync, Open, operations: installation.Operations);
            tasks.Show(this);
            await Task.Delay(200);
            tasks.GetVisualDescendants()
                .OfType<TabControl>()
                .Single()
                .SelectedIndex = 3;
            await Task.Delay(100);
            await Wait(
                () => Named<TextBox>(tasks, "NetworkParallel")
                    .Text != "4"
            );
            Check(true, "reopened-task-window-restores-network-settings");
            tasks.Close();
            tasks = null;
            var file = Path.Combine(paths.Root, "ui-drop.zip");
            using (var z = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(
                    z.CreateEntry("pack.mcmeta")
                        .Open()
                ))
                    w.Write("{\"pack\":{\"pack_format\":34,\"description\":\"UI\"}}");
                z.CreateEntry("assets/ui/readme.txt");
            }

            var importing = ImportDroppedAsync(file);
            await Wait(
                () => !OwnedWindows.Any(
                    w => w.Title == "导入本地资源"
                )
            );
            var dialog = OwnedWindows.Single(
                w => w.Title == "导入本地资源"
            );
            await Click(dialog, "检查并导入");
            await importing;
            var profile = (GameProfile)ProfileBox.SelectedItem!;
            Check(
                Directory.GetFiles(
                    Path.Combine(profile.GameDirectory, "resourcepacks"),
                    "*.zip"
                )
                    .Length > 0,
                "drop-confirm-button-imports-resource-through-host"
            );
            await NavigateClassicAsync("setup-launch");
            await Capture(this, "settings");
            Check(classicPage.IsVisible, "remaining-settings-page-renders-with-contract-models");
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
        }
        finally
        {
            tasks?.Close();
            Close();
        }
    }
}
