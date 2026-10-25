using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Pcl.Desktop;
public partial class MainWindow
{
    /// <summary>隔离目录里驱动真实实例页和导出按钮；成功后退出整个应用，供外部验证器独立运行脚本。</summary>
    private async Task OfflineScriptSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--offline-script-report") + 1];
        var checks = new List<string>();
        InstanceWorkspace? instanceWindow = null;
        OfflineScriptWindow? scriptWindow = null;
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
        }

        async Task Wait(Func<bool> pending)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (pending())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("UI wait");
                }

                await Task.Delay(100);
            }
        }

        try
        {
            if (!OperatingSystem.IsMacOS() || !paths.Root.Contains("verification-0.33", StringComparison.Ordinal) || accounts!.Accounts.Any(
                x => x.Kind != Pcl.Core.Contracts.Workspaces.AccountKind.Offline
            ))
            {
                throw new Exception("ISOLATED_ROOT_REQUIRED");
            }

            var profile = remote!.DesktopState.Profiles.First(
                x => x.GameVersion == "1.21.1" && x.Loader == "vanilla"
            );
            instanceWindow = new InstanceWorkspace(remote.Workspaces, remote.Components, profile.Id);
            instanceWindow.Show(this);
            await Wait(
                () => !instanceWindow.GetVisualDescendants()
                    .OfType<Button>()
                    .Any(
                        x => x.Content?.ToString() == "导出离线启动脚本"
                    )
            );
            var open = instanceWindow.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == "导出离线启动脚本"
                );
            Check(open.IsEffectivelyEnabled, "instance-page-export-button-enabled");
            open.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => !instanceWindow.OwnedWindows.OfType<OfflineScriptWindow>()
                    .Any()
            );
            scriptWindow = instanceWindow.OwnedWindows.OfType<OfflineScriptWindow>()
                .Single();
            scriptWindow.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "OfflineScriptPlayer"
                )
                .Text = "PCL033";
            var destination = Path.Combine(paths.Root, "UI standalone.command");
            scriptWindow.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "OfflineScriptDestination"
                )
                .Text = destination;
            scriptWindow.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == "导出脚本"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Wait(
                () => scriptWindow.Busy
            );
            Check(
                scriptWindow.ExportedPath == destination && File.Exists(destination),
                "actual-dialog-button-exports-via-host"
            );
            Check(
                (File.GetUnixFileMode(destination) & UnixFileMode.UserExecute) != 0,
                "ui-export-retains-executable-bit"
            );
            using var capture = new RenderTargetBitmap(
                new PixelSize((int)scriptWindow.Bounds.Width, (int)scriptWindow.Bounds.Height),
                new Vector(96, 96)
            );
            capture.Render(scriptWindow);
            capture.Save(
                Path.ChangeExtension(report, ".png"),
                PngBitmapEncoderOptions.Default
            );
            var firstBytes = await File.ReadAllBytesAsync(destination);
            scriptWindow.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == "导出脚本"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Wait(
                () => scriptWindow.Busy
            );
            var secondBytes = await File.ReadAllBytesAsync(destination);
            Check(
                firstBytes.SequenceEqual(secondBytes) && scriptWindow.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(
                        x => x.Name == "OfflineScriptStatus"
                    )
                    .Text!.Contains("已存在"),
                "ui-repeat-export-keeps-file-and-shows-actionable-error"
            );
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        script = destination,
                        hostPid = remote.Client.HostPid
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
            scriptWindow?.Close();
            instanceWindow?.Close();
            Close();
        }
    }
}
