using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Opt-in isolated acceptance drives the same dialog buttons and Host calls as the regular desktop.
    private async Task MainLinkSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--main-link-report") + 1];
        var checks = new List<string>();
        MultiplayerWindow? room = null;
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
            var deadline = DateTime.UtcNow.AddMinutes(4);
            while (pending())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("UI workflow timeout");
                }

                await Task.Delay(100);
            }
        }

        async Task Click(Window window, string text, Func<bool>? pending = null)
        {
            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == text
                );
            Check(button.IsEffectivelyEnabled, "ui-button-enabled-" + text);
            button.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Task.Delay(100);
            if (pending is not null)
            {
                await Wait(pending);
            }
        }

        T Named<T>(Window window, string name)
        where T : Control => window.GetVisualDescendants()
            .OfType<T>()
            .Single(
                x => x.Name == name
            );
        async Task Capture(Window window, string file)
        {
            await Task.Delay(150);
            using var bmp = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96)
            );
            bmp.Render(window);
            bmp.Save(
                Path.Combine(
                    Path.GetDirectoryName(report)!,
                    file + ".png"
                ),
                PngBitmapEncoderOptions.Default
            );
        }

        try
        {
            if (remote is null || !(paths.Root.Contains("architecture-0.31", StringComparison.Ordinal) || paths.Root.Contains("architecture-0.32", StringComparison.Ordinal)) || accounts!.Accounts.Any(
                x => x.Kind != Pcl.Core.Contracts.Workspaces.AccountKind.Offline
            ))
            {
                throw new InvalidOperationException("Isolated offline root required");
            }

            await NavigateClassicAsync("vanilla");
            Check(
                classicPage.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        x => x.Text?.StartsWith("\u6b63\u5f0f\u7248 (") == true
                    ),
                "ui-main-official-catalog-from-host"
            );
            await Capture(this, "versions");
            var install = ManageVersionsAsync("1.21.1");
            await Wait(
                () => !OwnedWindows.Any(
                    x => x.Title == "\u4e0b\u8f7d Minecraft"
                )
            );
            var dialog = OwnedWindows.Single(
                x => x.Title == "\u4e0b\u8f7d Minecraft"
            );
            var versions = dialog.GetVisualDescendants()
                .OfType<ListBox>()
                .Single();
            versions.SelectedItem = versions.Items.OfType<View.MinecraftRelease>()
                .Single(
                    x => x.Id == "1.21.1"
                );
            dialog.GetVisualDescendants()
                .OfType<ComboBox>()
                .Single()
                .SelectedIndex = 1;
            await Click(dialog, "\u4e0b\u8f7d\u5e76\u5b89\u88c5");
            await install;
            Check(
                lastInstallationResult?.Instance?.GameVersion == "1.21.1",
                "ui-main-version-dialog-installs-through-contract"
            );
            FabricClick(
                null,
                new RoutedEventArgs()
            );
            await Wait(
                () => !OwnedWindows.Any(
                    x => x.Title?.StartsWith("\u5b89\u88c5\u52a0\u8f7d\u5668") == true
                )
            );
            dialog = OwnedWindows.Single(
                x => x.Title?.StartsWith("\u5b89\u88c5\u52a0\u8f7d\u5668") == true
            );
            await Click(dialog, "\u9009\u62e9\u7248\u672c");
            await Wait(
                () => !OwnedWindows.Any(
                    x => x.Title == "Fabric · 1.21.1"
                )
            );
            dialog = OwnedWindows.Single(
                x => x.Title == "Fabric · 1.21.1"
            );
            Check(
                dialog.GetVisualDescendants()
                    .OfType<ComboBox>()
                    .Single()
                    .ItemCount > 0,
                "ui-host-loader-version-list"
            );
            await Capture(dialog, "loader-picker");
            await Click(dialog, "\u5b89\u88c5");
            await Wait(
                () => operation is not null
            );
            Check(
                (ProfileBox.SelectedItem as Pcl.Core.Contracts.Workspaces.GameProfile)?.Loader == "fabric",
                "ui-main-installs-selected-loader-in-host"
            );
            room = Bootstrap.WorkspacePages.Multiplayer(this, service);
            room.Show(this);
            await Task.Delay(150);
            Named<TextBox>(room, "LinkGamePort")
                .Text = "0";
            Named<TextBox>(room, "LinkPeers")
                .Text = "tcp://127.0.0.1:11010";
            await Click(
                room,
                "\u8fde\u63a5",
                () => room.Busy
            );
            Check(room.SessionId is null, "ui-invalid-room-input-does-not-start-process");
            Named<TextBox>(room, "LinkGamePort")
                .Text = "25565";
            await Click(
                room,
                "\u8fde\u63a5",
                () => room.Busy
            );
            var id = room.SessionId ?? throw new Exception("UI room did not start");
            Check(
                Named<TextBox>(room, "LinkInvitation")
                    .Text?.StartsWith("PCLMAC1:") == true,
                "ui-real-room-start-and-invitation"
            );
            // Do not capture a room credential; temporarily mask the displayed invitation for the evidence image.
            var invitation = Named<TextBox>(room, "LinkInvitation")
                .Text;
            Named<TextBox>(room, "LinkInvitation")
                .Text = "[invitation redacted]";
            await Capture(room, "multiplayer");
            Named<TextBox>(room, "LinkInvitation")
                .Text = invitation;
            await Click(
                room,
                "\u5237\u65b0\u8fde\u63a5\u72b6\u6001",
                () => room.Busy
            );
            Check(
                (await DesktopApi.CallAsync<View.LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id
                    }
                )).State == "running",
                "ui-refresh-reads-real-host-process-state"
            );
            room.Close();
            await Wait(
                () => room.IsVisible
            );
            Check(
                (await DesktopApi.CallAsync<View.LinkSnapshot>(
                    "workspace.link.status",
                    null,
                    new
                    {
                        id
                    }
                )).State == "stopped",
                "ui-window-close-awaits-host-room-stop"
            );
            room = null;
            var opti = Bootstrap.WorkspacePages.OptiFabric(this, service);
            opti.Show(this);
            await Capture(opti, "optifabric");
            opti.Close();
            Check(!opti.IsVisible, "ui-optifabric-contract-window-opens-and-closes");
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "passed",
                        Checks = checks
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
                        Status = "failed",
                        Checks = checks,
                        Error = ex.ToString()
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
            room?.Close();
            Close();
        }
    }
}
