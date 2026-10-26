using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 设置和个性化：读取、校验并应用用户配置，更新界面或处理本地文件入口，保存可恢复设置。
public partial class MainWindow
{
    private readonly LocalMusicPlayer musicPlayer = new();
    private Bitmap? backgroundBitmap;
    private string? displayedBackground;
    private AppearanceSettings appearance = new();
    // [实现] ApplyAppearance 的实现：执行 settings.Validate()；准备 Bitmap? next = null；准备 var imageFile = AppearanceFeatures.SelectBackground(settings, displayedBackground)。
    private void ApplyAppearance(AppearanceSettings settings)
    {
        // Native window decoration must not mimic sponsor-unlocked colour themes on Windows.
        if (OperatingSystem.IsWindows())
        {
            settings = settings with
            {
                Accent = "#0875D1",
                AccentMode = "static",
                TitleMode = "default",
                TitleImage = ""
            };
        }

        Bitmap? next = null;
        var imageFile = AppearanceFeatures.SelectBackground(settings, displayedBackground);
        if (imageFile.Length > 0)
        {
            // [实现] 条件成立（imageFile.Length > 0）时：按 new FileInfo(imageFile).Length > 30_000_000 分支处理；执行 next = new Bitmap(settings.Background)。
            if (new FileInfo(imageFile).Length > 30_000_000)
                throw new InvalidDataException("背景图片不能超过 30 MB。");
            next = new Bitmap(imageFile);
        }

        var app = Application.Current!;
        app.RequestedThemeVariant = settings.Theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        appearance = settings;
        Title = ProductInfo.Name;
        Opacity = settings.WindowOpacity;
        ApplyPalette();
        BackgroundImage.Source = next;
        BackgroundImage.Opacity = settings.BackgroundOpacity;
        BackgroundImage.Stretch = settings.BackgroundFit switch
        {
            "fit" => Stretch.Uniform,
            "stretch" => Stretch.Fill,
            "center" => Stretch.None,
            _ => Stretch.UniformToFill
        };
        BackgroundImage.Effect = settings.BackgroundBlur > 0
            ? new BlurEffect
            {
                Radius = settings.BackgroundBlur
            }

            : null;
        ApplyClassicAppearance(settings);
        backgroundBitmap?.Dispose();
        backgroundBitmap = next;
        displayedBackground = imageFile;
        foreach (var name in new[]
        {
            "NavMods",
            "NavShaders",
            "NavPacks",
            "NavDataPacks",
            "NavModpacks",
            "NavCurseForge",
            "NavManaged",
            "NavLocal",
            "NavPackTools"
        }

        )
            if (this.FindControl<Control>(name)is { } control)
                control.IsVisible = !settings.HiddenNavigation.Contains(name);
        if (AppearanceFeatures.ShouldPlayMusic(settings, game is not null))
        {
            // [实现] 条件成立（AppearanceFeatures.ShouldPlayMusic(settings, game is not null)）时：执行 musicPlayer.Play(settings)。
            musicPlayer.Play(settings);
        }
        else
        {
            // [实现] 前一条件不成立时：执行 musicPlayer.Stop()。
            musicPlayer.Stop();
        }
    }

    // [实现] ApplyPalette 的实现：准备 var dark = ActualThemeVariant == ThemeVariant.Dark；执行 Resources["PclText"] = Brush.Parse( dark ? "#E8ECF5" : "#172138" )；执行 Resources["PclMuted"] = Brush.Parse( dark ? "#A8B5CA" : "#52627A" )。
    private void ApplyPalette()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        Resources["PclText"] = Brush.Parse(
            dark
                ? "#E8ECF5"
                : "#172138"
        );
        Resources["PclMuted"] = Brush.Parse(
            dark
                ? "#A8B5CA"
                : "#52627A"
        );
        Resources["PclBackground"] = Brush.Parse(
            dark
                ? "#11151E"
                : "#F1F4FA"
        );
        Resources["PclSidebar"] = Brush.Parse(
            dark
                ? "#161C28"
                : "#E6ECF7"
        );
        Resources["PclCard"] = Brush.Parse(
            dark
                ? "#E61C2330"
                : "#EEFFFFFF"
        );
        Resources["PclAccent"] = Brush.Parse(appearance.Accent);
        Resources["PclSelection"] = Brush.Parse(
            dark
                ? "#29354E"
                : "#CDD9F2"
        );
        ApplyWindowsPalette();
    }

    // [实现] CustomHomeClick 的实现：执行受异常处理保护的操作。
    private async void CustomHomeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var page = await new AppearanceService(paths).ReadHomeAsync()；准备 var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 }；执行 panel.Children.Add( new TextBlock { Text = page.Title, FontSize = 24 } )。
            var page = await new Pcl.Client.WorkspaceApi(
                Bootstrap.WorkspacePages.Operations(this, service)
            ).CallAsync<HomeDocument>("workspace.settings.home", null);
            var panel = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16
            };
            panel.Children.Add(
                new TextBlock
                {
                    Text = page.Title,
                    FontSize = 24
                }
            );
            panel.Children.Add(
                new TextBlock
                {
                    Text = page.Body,
                    TextWrapping = TextWrapping.Wrap
                }
            );
            var window = new WorkspaceWindow
            {
                Title = page.Title,
                Width = 640,
                Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = panel
                }
            };
            if (page.Markup is not null)
            {
                panel.Children.Clear();
                panel.Children.Add(
                    HomeMarkupView.Build(
                        page.Markup,
                        link => ExecuteHomeAction(
                            link,
                            new Button(),
                            window
                        )
                    )
                );
            }

            foreach (var link in page.Markup is null
                ? page.Links
                : [])
            {
                // [实现] 对 page.Links 中的每一项：准备 var actionName = link.Kind switch { "copy" => "复制", "message" => "查看消息", "navigate" => "打开页面", …；准备 var button = new Button { Content = actionName + " · " + link.Text }；执行 ToolTip.SetTip(button, link.Url)。
                var actionName = link.Kind switch
                {
                    "copy" => "复制",
                    "message" => "查看消息",
                    "navigate" => "打开页面",
                    "launch" => "启动游戏",
                    "refresh" => "刷新",
                    "join_room" => "联机",
                    _ => "打开网页"
                };
                var button = new Button
                {
                    Content = actionName + " · " + link.Text
                };
                ToolTip.SetTip(button, link.Url);
                button.Click += async (_, _) => await ExecuteHomeAction(link, button, window);
                panel.Children.Add(button);
            }

            await window.PresentAsync(this);
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 StatusText.Text = ex.Message。
            StatusText.Text = ex.Message;
        }
    }

    // JSON 与 XAML 主页共用导航、启动、复制及刷新逻辑，错误留在主窗口状态栏。
    private async Task ExecuteHomeAction(HomeLink link, Button button, Window? window = null)
    {
        try
        {
            // [实现] 在异常保护范围内：按 link.Kind == "url" 分支处理。
            if (link.Kind == "url")
                Open(link.Url);
            else if (link.Kind == "copy")
            {
                // [实现] 条件成立（link.Kind == "copy"）时：按 Clipboard is not null 分支处理；执行 button.Content = "已复制 · " + link.Text。
                if (Clipboard is not null)
                    await Clipboard.SetTextAsync(link.Url);
                button.Content = "已复制 · " + link.Text;
            }
            else if (link.Kind == "message")
                await new WorkspaceWindow
                {
                    Title = link.Text,
                    Width = 440,
                    Height = 300,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Margin = new Thickness(24),
                            Text = link.Url,
                            TextWrapping = TextWrapping.Wrap
                        }
                    },
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                }.PresentAsync(window ?? this);
            else
            {
                // [实现] 前一条件不成立时：执行 window.Close()；按 link.Kind == "refresh" 分支处理。
                window?.Close();
                if (link.Kind == "refresh")
                {
                    await RefreshClassicHomeAsync();
                    if (window is not null)
                    {
                        CustomHomeClick(
                            null,
                            new RoutedEventArgs()
                        );
                    }
                }
                else if (link.Kind == "join_room")
                {
                    await Bootstrap.WorkspacePages.Multiplayer(this, service, link.Url)
                        .PresentAsync(this);
                }
                else if (link.Kind == "launch")
                {
                    // [实现] 条件成立（link.Kind == "launch"）时：准备 var profile = service.DesktopState.Profiles .FirstOrDefault( x => x.Id == link.Url ) ?? throw new InvalidOp…；执行 RefreshProfiles(profile.Id)；执行 await RefreshInstalledAsync()。
                    var profile = service.DesktopState.Profiles.FirstOrDefault(
                        x => x.Id == link.Url
                    ) ?? throw new InvalidOperationException("此主页引用的实例未安装。");
                    RefreshProfiles(profile.Id);
                    await RefreshInstalledAsync();
                    if (!LaunchButton.IsEnabled)
                        throw new InvalidOperationException("请选择账户并完成实例安装。");
                    LaunchButton.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                }
                else if (link.Kind == "navigate")
                    switch (link.Url)
                    {
                        case "instances":
                            await Bootstrap.WorkspacePages.OpenInstancesAsync(
                                this,
                                service,
                                null,
                                id => RefreshProfiles(id)
                            );
                            break;
                        case "downloads":
                            await ManageVersionsAsync();
                            break;
                        case "tasks":
                            await new TaskCenterWindow(
                                service,
                                RunAsync,
                                Open,
                                () => operation?.Cancel()
                            ).PresentAsync(this);
                            break;
                        case "settings":
                            await Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open)
                                .PresentAsync(this);
                            break;
                        case "resources":
                            if (ProfileBox.SelectedItem is GameProfile selected)
                                await Bootstrap.WorkspacePages.Local(this, service, selected)
                                    .PresentAsync(this);
                            break;
                        case "packs":
                            if (ProfileBox.SelectedItem is GameProfile pack)
                                await Bootstrap.WorkspacePages.Pack(
                                    this,
                                    service,
                                    pack,
                                    p => RefreshProfiles(p.Id)
                                )
                                    .PresentAsync(this);
                            break;
                        case "network":
                            await Bootstrap.WorkspacePages.Cache(this, paths)
                                .PresentAsync(this);
                            break;
                    }
            }
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 StatusText.Text = ex.Message。
            StatusText.Text = ex.Message;
        }
    }
}
