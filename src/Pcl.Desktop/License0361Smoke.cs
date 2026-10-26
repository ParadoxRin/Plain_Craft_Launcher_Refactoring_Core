using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts;
using Pcl.Core.Contracts.Launch;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Explicit disposable UI test: real windows/Host, no personal data or browser navigation.
    private async Task License0361SmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new Exception(label);
            }

            checks.Add(label);
        }

        void Capture(Window window, string name)
        {
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(window);
            bitmap.Save(
                Path.Combine(
                    Path.GetDirectoryName(report)!,
                    name
                )
            );
        }

        try
        {
            if (!(paths.Root.Contains("verification-0.36.1") || paths.Root.Contains("verification-0.38")) || !paths.Root.EndsWith("license-ui-data"))
            {
                throw new InvalidOperationException("ISOLATED_LICENSE_TEST_ROOT_REQUIRED");
            }

            Check(ProductInfo.Version == "0.38.0", "three-component-version");
            Check(Title == ProductIdentity.Name, "product-name-in-window");
            var about = BuildAboutContent();
            Check(
                ((TextBlock)about.Children[0]).Text!.Contains("龙腾猫跃"),
                "upstream-author-first"
            );
            Check(
                ((Button)about.Children[1]).Content!.ToString()!.Contains("赞助"),
                "upstream-sponsorship-second"
            );
            Check(
                about.Children.OfType<TextBlock>()
                    .Any(
                        x => x.Text == ProductIdentity.ThirdPartyNotice
                    ),
                "explicit-independent-third-party-notice"
            );
            Check(
                about.Children.OfType<TextBlock>()
                    .Any(
                        x => x.Text == ProductIdentity.SourceStatus
                    ),
                "private-source-limitation-visible"
            );
            var aboutWindow = new WorkspaceWindow
            {
                Title = "关于 · " + ProductInfo.Name,
                Width = 760,
                Height = 720,
                Content = new ScrollViewer
                {
                    Content = about
                }
            };
            var showing = aboutWindow.ShowDialog(this);
            await Task.Delay(350);
            Capture(aboutWindow, "about.png");
            aboutWindow.Close();
            await showing;
            var account = await AddOfflineAccountAsync("License_Test", CancellationToken.None);
            var guide = await service.Client.Call<LaunchGuidance>(
                "game.guidance",
                new LaunchGuidanceRequest(account.Id, "zh-CN")
            );
            Check(
                guide.ShowSponsor && guide.ShowPurchase && !guide.RequireDemo,
                "real-host-chinese-guidance"
            );
            Check(
                guide.SponsorUrl == ProductIdentity.SponsorUrl && guide.PurchaseUrl == ProductIdentity.PurchaseUrl,
                "official-destinations"
            );
            int opened = 0;
            var window = new LaunchGuidanceWindow(
                guide,
                "zh-CN",
                _ => opened++
            );
            var choosing = window.ShowDialog<LaunchGuidanceConsent?>(this);
            await Task.Delay(350);
            Capture(window, "launch-guidance.png");
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "SponsorLink"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "PurchaseLink"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            Check(opened == 2 && !choosing.IsCompleted, "links-do-not-authorize-launch");
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "GuidanceCancel"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            Check(await choosing is null, "cancel-returns-no-consent");
            Check(
                !Directory.Exists(
                    Path.Combine(paths.Root, "launch-operations-v1")
                ) || !Directory.EnumerateFiles(
                    Path.Combine(paths.Root, "launch-operations-v1"),
                    "*.json",
                    SearchOption.AllDirectories
                )
                    .Any(),
                "cancel-does-not-submit-game-launch"
            );
            window = new LaunchGuidanceWindow(
                guide,
                "zh-CN",
                _ => opened++
            );
            choosing = window.ShowDialog<LaunchGuidanceConsent?>(this);
            await Task.Delay(100);
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "GuidanceContinue"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            var consent = await choosing;
            Check(
                consent is { SponsorRead: true, PurchaseRead: true, Demo: false },
                "continue-returns-explicit-consent"
            );
            var english = await service.Client.Call<LaunchGuidance>(
                "game.guidance",
                new LaunchGuidanceRequest(account.Id, "en-US")
            );
            Check(english.RequireDemo, "real-host-non-chinese-trial-required");
            window = new LaunchGuidanceWindow(
                english,
                "en-US",
                _ => opened++
            );
            choosing = window.ShowDialog<LaunchGuidanceConsent?>(this);
            await Task.Delay(100);
            Check(
                !window.GetVisualDescendants()
                    .OfType<Button>()
                    .Any(
                        x => x.Name == "GuidanceContinue"
                    ),
                "no-full-offline-button-in-restricted-flow"
            );
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "GuidanceDemo"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            Check(await choosing is { Demo: true }, "trial-choice-is-explicit");
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        gameStarted = false,
                        networkAuthorization = false
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.Message
                    }
                )
            );
            Environment.ExitCode = 1;
        }
        finally
        {
            Close();
        }
    }
}
