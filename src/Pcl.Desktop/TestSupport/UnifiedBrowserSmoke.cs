using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 显式验收入口只允许带标记的测试实例；使用真实窗口与 Host，不读取个人凭据。
    private async Task UnifiedBrowserSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        UnifiedSearchWindow? page = null;
        try
        {
            if (!File.Exists(
                Path.Combine(paths.Root, ".unified-browser-test")
            ) || (accounts?.Accounts.Any() ?? true))
            {
                throw new InvalidOperationException("ISOLATED_EMPTY_ACCOUNT_ROOT_REQUIRED");
            }

            var profile = service.DesktopState.Profiles.Single(
                x => x.Id == "unified-test"
            );
            page = new UnifiedSearchWindow(
                service.Workspaces,
                profile,
                _ =>
                {
                },
                _ =>
                {
                }
            );
            page.Show(this);
            var checks = await page.AcceptanceAsync();
            await Task.Delay(500);
            page.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)page.Bounds.Width, (int)page.Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(page);
            bitmap.Save(
                Path.ChangeExtension(report, ".png"),
                PngBitmapEncoderOptions.Default
            );
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        scope = "Real UI/Host/public Modrinth query and mod installation into a synthetic isolated instance; no game run or Microsoft account"
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
                        error = ex.Message
                    }
                )
            );
            Environment.ExitCode = 1;
        }
        finally
        {
            page?.Close();
            Close();
        }
    }
}

public sealed partial class UnifiedSearchWindow
{
    internal async Task<string[]> AcceptanceAsync()
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name + ": " + status.Text);
            }

            checks.Add(name);
        }

        Check(
            allVersions.IsChecked == true && loader.SelectedIndex == 0 && source.SelectedIndex == 0,
            "all-filter-defaults"
        );
        query.Text = "fabric-api";
        await Run(
            token => Search(0, token)
        );
        Check(session is { Hits.Length: > 0 }, "real-public-search-results");
        Check(
            session!.SourceStatus.Any(
                x => x.Contains("CurseForge") && x.Contains("未配置")
            ),
            "unavailable-source-visible"
        );
        var hit = session.Hits.FirstOrDefault(
            x => x.WebUrl?.EndsWith("/fabric-api") == true
        );
        Check(hit is not null, "explicit-project-result");
        results.SelectedItem = hit;
        Check(
            !downloadChoices.IsVisible && versions.ItemsSource is null,
            "selection-does-not-fetch-downloads"
        );
        await Run(LoadVersions);
        Check(
            downloadChoices.IsVisible && availableReleases.Any(
                x => x.Source == "Modrinth"
            ),
            "download-options-loaded-on-request"
        );
        Check(
            availableReleases.All(
                x => x.GameVersions.Contains("1.21.1") && x.Loaders.Contains("fabric")
            ),
            "versions-match-same-target-instance"
        );
        downloadSource.SelectedIndex = 2;
        Check(
            !versions.ItemsSource!.Cast<object>()
                .Any(),
            "unavailable-cf-does-not-relabel-mr"
        );
        downloadSource.SelectedIndex = 1;
        Check(
            versions.SelectedItem is SearchRelease { Source: "Modrinth" },
            "source-choice-retains-file-identity"
        );
        await Run(Install);
        Check(
            status.Text?.StartsWith("已安装并校验") == true,
            "real-host-installs-selected-mod"
        );
        Check(
            Directory.EnumerateFiles(
                Path.Combine(profile.GameDirectory, "mods"),
                "*.jar"
            )
                .Any(),
            "installed-jar-exists"
        );
        source.SelectedIndex = 4;
        await Run(
            token => Search(0, token)
        );
        Check(
            session!.Hits.Any(
                x => x.LocalDirectory is not null
            ),
            "installed-resource-visible-in-local-source"
        );
        results.SelectedItem = session.Hits.First(
            x => x.LocalDirectory is not null
        );
        await Run(LoadVersions);
        Check(availableReleases.Length > 0, "installed-project-can-reopen-version-picker");
        await Task.Delay(300);
        UpdateLayout();
        Check(
            results.ContainerFromIndex(0)is not null,
            "result-card-realized-in-visible-list"
        );
        return checks.ToArray();
    }
}
