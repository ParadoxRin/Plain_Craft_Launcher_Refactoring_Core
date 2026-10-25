using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 在隔离目录运行真实窗口，逐页检查导航、搜索、详情、返回与对照表，并保存可复核截图。
public partial class MainWindow
{
    // [实现] WindowsLayoutSmokeAsync 的实现：准备 var checks = new List<object>()；执行受异常处理保护的操作。
    private async Task WindowsLayoutSmokeAsync()
    {
        var checks = new List<object>();
        try
        {
            // [实现] Capture 的实现：执行 await Task.Delay(650)；准备 var bitmap = new RenderTargetBitmap( new PixelSize((int)Bounds.Width, (int)Bounds.Height), new …；执行 bitmap.Render(this)。
            async Task Capture(string name)
            {
                await Task.Delay(650);
                using var bitmap = new RenderTargetBitmap(
                    new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                    new Vector(96, 96)
                );
                bitmap.Render(this);
                bitmap.Save(
                    Path.Combine(paths.Evidence, name + ".png"),
                    PngBitmapEncoderOptions.Default
                );
                Console.WriteLine("CLASSIC_CAPTURE " + name);
            }

            // [实现] Check 的实现：按 !condition 分支处理；执行 checks.Add( new { name, status = "passed" } )。
            void Check(bool condition, string name)
            {
                if (!condition)
                {
                    // [实现] 条件成立（!condition）时：抛出 new InvalidOperationException(name)。
                    throw new InvalidOperationException(name);
                }

                checks.Add(
                    new
                    {
                        name,
                        status = "passed"
                    }
                );
            }

            Check(
                classicTabs.Children.OfType<Button>()
                    .Select(
                        x => x.Tag?.ToString()
                    )
                    .SequenceEqual(
                        new[]
                        {
                            "launch",
                            "download",
                            "settings",
                            "more"
                        }
                    ),
                "four original top-level routes"
            );
            // 通过已重排的真实控件保存离线名称，验证新布局没有切断账户绑定。
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCL_Compare";
            LoginButton.RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)
            );
            for (int i = 0; i < 100 && operation is not null; i++)
            {
                // [实现] 每次迭代（i < 100 && operation is not null）：执行 await Task.Delay(50)。
                await Task.Delay(50);
            }

            Check(
                accounts?.Accounts.Any(
                    x => x.Kind == AccountKind.Offline
                ) == true,
                "offline account saved through new layout"
            );
            AccountTypeBox.SelectedIndex = 1;
            Check(
                MicrosoftLoginInfo.IsVisible && !OfflineNameBox.IsVisible,
                "Microsoft login mode switches controls"
            );
            AccountTypeBox.SelectedIndex = 0;
            await Capture("01-launch");
            await NavigateClassicAsync("vanilla");
            Check(
                classicNavigation.Children.OfType<Button>()
                    .Count() == 6,
                "six original download categories"
            );
            await Capture("02-vanilla");
            Check(
                classicPage.GetVisualDescendants()
                    .OfType<Expander>()
                    .Count() >= 4,
                "live Minecraft release groups"
            );
            foreach (var(route, query)in new[]
            {
                ("mod", "Sodium"),
                ("shader", "Complementary")
            }

            )
            {
                // [实现] 本段流程：执行 await NavigateClassicAsync(route)；执行 await resources.ClassicSearchForSmokeAsync(query)；执行 Check(resources.ClassicProjectCount > 0, route + " live search")。
                await NavigateClassicAsync(route);
                await resources.ClassicSearchForSmokeAsync(query);
                Check(resources.ClassicProjectCount > 0, route + " live search");
                await Capture(route + "-list");
                await resources.ClassicOpenFirstForSmokeAsync();
                Check(resources.ClassicVersionGroupCount > 0, route + " grouped versions");
                Check(
                    classicBack.IsVisible && classicBody.ColumnDefinitions[0].Width.Value == 0,
                    route + " independent detail page"
                );
                await Capture(route + "-detail");
                await resources.ClassicSelectFileForSmokeAsync();
                Check(
                    resources.ClassicActionsVisible,
                    route + " file selection exposes real install and save actions"
                );
                await Capture(route + "-file-actions");
                Check(
                    resources.ReturnToClassicSearch(),
                    route + " back to preserved results"
                );
            }

            foreach (var route in new[]
            {
                "setup-launch",
                "setup-ui",
                "setup-system",
                "setup-link",
                "help",
                "about",
                "toolbox",
                "feedback",
                "vote",
                "matrix",
                "extensions"
            }

            )
            {
                // [实现] 对 new[] { "setup-launch", "setup-ui", "setup-system", "setup-link", "help", "about", "toolbox", "… 中的每一项：执行 await NavigateClassicAsync(route)；执行 Check(classicPage.IsVisible && classicPage.Content is not null, route + " reachable")；执行 await Capture(route)。
                await NavigateClassicAsync(route);
                Check(classicPage.IsVisible && classicPage.Content is not null, route + " reachable");
                await Capture(route);
            }

            await NavigateClassicAsync("launch");
            Check(
                HomePanel.IsVisible && ProfileBox.Parent is not null && AccountTypeBox.Parent is not null && LaunchButton.Parent is not null,
                "launch bindings survive route roundtrip"
            );
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "windows-layout-ui.json"),
                new
                {
                    status = "passed",
                    checks,
                    source = "live APIs; actual Avalonia window; no mock results",
                    gameLaunch = "not repeated for UI-only release"
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "windows-layout-ui.json"), new { status = "failed…；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "windows-layout-ui.json"),
                new
                {
                    status = "failed",
                    checks,
                    error = ex.ToString()
                }
            );
            Console.Error.WriteLine(ex);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
