using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Client;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Explicit opt-in only: exercise real Avalonia buttons and Host in a disposable offline data root.
    private async Task BrowserAccountSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--browser-account-report") + 1];
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new Exception(label);
            }

            checks.Add(label);
        }

        async Task Wait(Func<bool> pending)
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (pending())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("UI operation did not finish");
                }

                await Task.Delay(100);
            }
        }

        async Task Click(Window window, string title, Func<bool> pending)
        {
            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == title
                );
            Check(button.IsEffectivelyEnabled, "enabled-button-" + title);
            button.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Task.Delay(100);
            await Wait(pending);
        }

        async Task Capture(Window window, string name)
        {
            await Task.Delay(200);
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(window);
            bitmap.Save(
                Path.Combine(
                    Path.GetDirectoryName(report)!,
                    name + ".png"
                ),
                PngBitmapEncoderOptions.Default
            );
        }

        SkinManagerWindow? skinWindow = null;
        AccountToolsWindow? toolsWindow = null;
        try
        {
            if (remote is null || !(paths.Root.Contains("architecture-0.30", StringComparison.Ordinal) || (paths.Root.Contains("architecture-0.31", StringComparison.Ordinal) || (paths.Root.Contains("architecture-0.32", StringComparison.Ordinal) || paths.Root.Contains("verification-0.35", StringComparison.Ordinal)))) || accounts!.Accounts.Any(
                x => x.Kind != Pcl.Core.Contracts.Workspaces.AccountKind.Offline
            ))
            {
                throw new InvalidOperationException("Isolated offline Host root required");
            }

            var api = AccountApi;
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCL030_UI";
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Task.Delay(100);
            await Wait(
                () => operation is not null
            );
            var index = await api.CallAsync<View.AccountPageSnapshot>("workspace.account.list", null);
            var account = index.Accounts.Single(
                x => x.Name == "PCL030_UI"
            );
            Check(index.SelectedId == account.Id, "ui-offline-login-button-creates-host-account");
            var second = await api.CallAsync<View.LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL030_UI_2"
                }
            );
            RefreshAccounts(second.Id);
            AccountBox.SelectedItem = accounts.Accounts.Single(
                x => x.Id == account.Id
            );
            await Task.Delay(100);
            await Wait(
                () => operation is not null
            );
            Check(
                (await api.CallAsync<View.AccountPageSnapshot>("workspace.account.list", null)).SelectedId == account.Id,
                "ui-account-combo-persists-selection-through-host"
            );
            skinWindow = Bootstrap.WorkspacePages.Skins(this, accounts);
            skinWindow.Show(this);
            await Task.Delay(100);
            await Wait(
                () => skinWindow.Busy
            );
            var name = skinWindow.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "PublicSkinPlayerName"
                );
            name.Text = "Notch";
            await Click(
                skinWindow,
                "\u83b7\u53d6\u6b63\u7248\u73a9\u5bb6\u76ae\u80a4",
                () => skinWindow.Busy
            );
            Check(
                skinWindow.GetVisualDescendants()
                    .OfType<Image>()
                    .Any(
                        x => x.Source is not null
                    ),
                "ui-public-skin-preview-rendered"
            );
            await Click(
                skinWindow,
                "\u4fdd\u5b58\u83b7\u53d6\u7684\u76ae\u80a4",
                () => skinWindow.Busy
            );
            Check(
                (await api.CallAsync<View.SkinState>(
                    "workspace.account.skinState",
                    null,
                    new
                    {
                        accountId = account.Id
                    }
                )).PlayerName == "Notch",
                "ui-save-fetched-skin-button"
            );
            await Click(
                skinWindow,
                "\u5237\u65b0\u6b63\u7248\u76ae\u80a4",
                () => skinWindow.Busy
            );
            Check(
                skinWindow.Status023.Contains("\u5df2\u91cd\u65b0"),
                "ui-refresh-skin-button"
            );
            await Capture(skinWindow, "skin-page");
            skinWindow.Close();
            skinWindow = Bootstrap.WorkspacePages.Skins(this, accounts);
            skinWindow.Show(this);
            await Task.Delay(100);
            await Wait(
                () => skinWindow.Busy
            );
            Check(
                skinWindow.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(
                        x => x.Name == "PublicSkinPlayerName"
                    )
                    .Text == "Notch" && skinWindow.GetVisualDescendants()
                    .OfType<Image>()
                    .Any(
                        x => x.Source is not null
                    ),
                "ui-reopened-skin-page-restores-name-and-preview"
            );
            await Click(
                skinWindow,
                "\u6062\u590d\u9ed8\u8ba4\u76ae\u80a4",
                () => skinWindow.Busy
            );
            Check(
                (await api.CallAsync<View.SkinState>(
                    "workspace.account.skinState",
                    null,
                    new
                    {
                        accountId = account.Id
                    }
                )).Png is null,
                "ui-reset-offline-skin-button"
            );
            skinWindow.Close();
            skinWindow = null;
            toolsWindow = Bootstrap.WorkspacePages.Accounts(this, accounts);
            toolsWindow.Show(this);
            await Task.Delay(100);
            await Wait(
                () => toolsWindow.Busy
            );
            Check(
                !toolsWindow.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Single()
                    .Items.Any(),
                "ui-external-account-page-empty-index-without-keychain"
            );
            await Capture(toolsWindow, "external-account-page");
            toolsWindow.Close();
            toolsWindow = null;
            await NavigateClassicAsync("mod");
            await resources.ClassicSearchForSmokeAsync("sodium");
            await resources.WaitIconsForSmokeAsync();
            Check(
                resources.ProjectCountForSmoke > 0 && resources.IconCountForSmoke > 0,
                "ui-default-resource-search-and-host-icons"
            );
            await Capture(this, "resource-search");
            await resources.ClassicOpenFirstForSmokeAsync();
            await resources.WaitIconsForSmokeAsync();
            Check(
                resources.ReleaseCountForSmoke > 0 && resources.DescriptionForSmoke.Length > 0,
                "ui-default-resource-detail-and-release-list"
            );
            await resources.ClassicSelectFileForSmokeAsync();
            Check(resources.SelectedReleaseForSmoke, "ui-resource-file-row-selection");
            await Capture(this, "resource-detail");
            Check(
                resources.ReturnToClassicSearch(),
                "ui-return-to-resource-search"
            );
            await NavigateClassicAsync("shader");
            await resources.ClassicSearchForSmokeAsync("Complementary");
            await resources.WaitIconsForSmokeAsync();
            Check(resources.ProjectCountForSmoke > 0, "ui-default-shader-browser-live-query");
            Check(
                !resources.BackendHintVisibleForSmoke,
                "ui-does-not-invent-backend-warning-without-installed-backend"
            );
            await Capture(this, "shader-search");
            await RunAsync(
                ct => EnsureFabricAsync(ct)
            );
            await NavigateClassicAsync("mod");
            resources.FilterForSmoke("1.21.1", 1);
            await resources.ClassicSearchForSmokeAsync("Fabric API");
            await resources.WaitIconsForSmokeAsync();
            await resources.ClassicOpenFirstForSmokeAsync();
            await resources.WaitIconsForSmokeAsync();
            await resources.ClassicSelectFileForSmokeAsync();
            Check(resources.SelectedReleaseForSmoke, "ui-compatible-mod-file-selected");
            resources.InstallForSmoke();
            await Task.Delay(100);
            await Wait(
                () => operation is not null
            );
            var profile = (Pcl.Core.Contracts.Workspaces.GameProfile)ProfileBox.SelectedItem!;
            var installed = await api.CallAsync<View.ManagedResource[]>("workspace.browser.display", profile.Id);
            Check(
                installed.Any(
                    x => x.Name == "Fabric API" && File.Exists(
                        Path.Combine(profile.GameDirectory, "mods", x.FileName)
                    )
                ),
                "ui-default-browser-installs-real-fabric-api-through-host"
            );
            await ShowResourceAsync(Pcl.Core.Contracts.Workspaces.ResourceKind.Mod, NavManaged, true);
            Check(resources.ProjectCountForSmoke > 0, "ui-managed-list-uses-host-display-records");
            await Capture(this, "installed-resource");
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
            skinWindow?.Close();
            toolsWindow?.Close();
            Close();
        }
    }
}

public sealed partial class ResourceBrowser
{
    // Read-only UI evidence, not substitutes for catalog results or button actions.
    internal void FilterForSmoke(string version, int loader)
    {
        browseVersion.Text = version;
        browseLoader.SelectedIndex = loader;
    }

    internal void InstallForSmoke() => install.RaiseEvent(
        new RoutedEventArgs(Button.ClickEvent)
    );
    internal bool BackendHintVisibleForSmoke => compatibility.IsVisible;
    internal int ProjectCountForSmoke => projects.ItemCount;
    internal int IconCountForSmoke => classicIcons.Count;
    internal int ReleaseCountForSmoke => releases.ItemCount;
    internal string DescriptionForSmoke => description.Text ?? "";
    internal bool SelectedReleaseForSmoke => selectedRelease is not null;

    internal Task WaitIconsForSmokeAsync() => Task.WhenAll(
        iconTasks.ToArray()
    );
}
