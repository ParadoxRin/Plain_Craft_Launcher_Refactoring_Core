using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// Windows 对照壳复用现有业务控件，仅重组导航与页面；旧版布局仍可用 --modern-layout 打开。
public partial class MainWindow
{
    private bool WindowsLayout => !Program.Arguments.Contains("--modern-layout");

    private readonly StackPanel classicNavigation = new()
    {
        Spacing = 0,
        Margin = new Thickness(0, 12)
    };
    private readonly ContentControl classicPage = new();
    private readonly ContentControl classicCustomHome = new()
    {
        IsVisible = false
    };
    private int classicHomeRevision;
    private readonly Grid classicBody = new()
    {
        ColumnDefinitions = ColumnDefinitions.Parse("150,*")
    };
    private readonly StackPanel classicTabs = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center
    };
    private readonly TextBlock classicDetailTitle = new()
    {
        Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button classicBack = new()
    {
        Content = "←",
        Foreground = Brushes.White,
        Background = Brushes.Transparent,
        IsVisible = false
    };
    private Grid? classicHeader;
    private TextBlock? classicBrand;
    private bool classicStylesAdded;
    private string classicSection = "launch";
    private string classicRoute = "launch";
    private int classicPageRevision;
    private static readonly JsonObject WindowsBaseline = ReadEmbeddedJson("windows-layout.json");
    // 资源从应用程序集读取，移动 .app 后也能使用原版页面清单与功能矩阵。
    private static JsonObject ReadEmbeddedJson(string name)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(name) ?? throw new InvalidDataException("缺少界面对照资源：" + name);
        return JsonNode.Parse(stream)!.AsObject();
    }

    // 将已绑定的控件移出旧容器，保留名称、事件和状态，避免复制第二套登录/启动实现。
    internal static T MoveControl<T>(T control)
    where T : Control
    {
        switch (control.Parent)
        {
            case Panel panel:
                panel.Children.Remove(control);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl content:
                content.Content = null;
                break;
        }

        Grid.SetRow(control, 0);
        Grid.SetColumn(control, 0);
        Grid.SetRowSpan(control, 1);
        Grid.SetColumnSpan(control, 1);
        control.Margin = new Thickness(0);
        return control;
    }

    // 统一使用原版蓝白配色、小圆角和紧凑行高；不覆盖用户保存的外观设置文件。
    private void ApplyWindowsPalette()
    {
        if (!WindowsLayout)
        {
            // [实现] 条件成立（!WindowsLayout）时：结束当前过程。
            return;
        }

        RequestedThemeVariant = ThemeVariant.Light;
        Resources["PclText"] = Brush.Parse("#303B45");
        Resources["PclMuted"] = Brush.Parse("#7D8791");
        Resources["PclBackground"] = Brush.Parse("#DCEAF9");
        Resources["PclSidebar"] = Brushes.White;
        Resources["PclCard"] = Brushes.White;
        Resources["PclAccent"] = Brush.Parse("#0875D1");
        Resources["PclSelection"] = Brush.Parse("#E7F2FF");
        Title = appearance.Title;
        Resources["PclAccent"] = Brush.Parse(appearance.Accent);
        if (classicStylesAdded)
        {
            // [实现] 条件成立（classicStylesAdded）时：结束当前过程。
            return;
        }

        classicStylesAdded = true;
        foreach (var type in new[]
        {
            typeof(Button),
            typeof(ComboBox),
            typeof(TextBox)
        }

        )
        {
            // [实现] 对 new[] { typeof(Button), typeof(ComboBox), typeof(TextBox) } 中的每一项：准备 var style = new Style( x => x.OfType(type) )；执行 style.Setters.Add( new Setter( TemplatedControlCornerRadius(type), new CornerRadius(3) ) )；执行 Styles.Add(style)。
            var style = new Style(
                x => x.OfType(type)
            );
            style.Setters.Add(
                new Setter(
                    TemplatedControlCornerRadius(type),
                    new CornerRadius(3)
                )
            );
            Styles.Add(style);
        }

        var buttons = new Style(
            x => x.OfType<Button>()
        );
        buttons.Setters.Add(
            new Setter(
                Button.PaddingProperty,
                new Thickness(14, 8)
            )
        );
        buttons.Setters.Add(
            new Setter(Button.FontSizeProperty, 13d)
        );
        buttons.Setters.Add(
            new Setter(Button.BackgroundProperty, Brushes.White)
        );
        buttons.Setters.Add(
            new Setter(
                Button.BorderBrushProperty,
                Brush.Parse("#718191")
            )
        );
        buttons.Setters.Add(
            new Setter(
                Button.BorderThicknessProperty,
                new Thickness(1)
            )
        );
        Styles.Add(buttons);
        var cards = new Style(
            x => x.OfType<Border>()
                .Class("card")
        );
        cards.Setters.Add(
            new Setter(
                Border.CornerRadiusProperty,
                new CornerRadius(6)
            )
        );
        cards.Setters.Add(
            new Setter(
                Border.PaddingProperty,
                new Thickness(18)
            )
        );
        Styles.Add(cards);
    }

    // 所有这些模板控件共用 CornerRadius 属性，保持样式类型正确。
    private static AvaloniaProperty TemplatedControlCornerRadius(Type type)
    {
        return Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty;
    }

    // 顶栏和左栏顺序来自 Windows 源码清单，Mac 扩展集中放在“更多”下。
    private void InitializeWindowsLayout()
    {
        if (!WindowsLayout)
        {
            // [实现] 条件成立（!WindowsLayout）时：结束当前过程。
            return;
        }

        Width = 1100;
        Height = 780;
        MinWidth = 900;
        MinHeight = 640;
        ApplyWindowsPalette();
        MoveControl(HomePanel);
        MoveControl(ResourcePanel);
        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("48,*")
        };
        var header = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("290,*,100"),
            Background = Brush.Parse("#126CC5")
        };
        var brand = new TextBlock
        {
            Text = ProductInfo.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 14,
            Foreground = Brushes.White,
            Margin = new Thickness(18, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(brand);
        foreach (var(key, label)in new[]
        {
            ("launch", "▷  启动"),
            ("download", "↓  下载"),
            ("settings", "⚙  设置"),
            ("more", "▦  更多")
        }

        )
        {
            // [实现] 本段流程：准备 var button = ClassicButton( label, () => NavigateClassicAsync(key) )；执行 button.Tag = key；执行 button.BorderThickness = new Thickness(0)。
            var button = ClassicButton(
                label,
                () => NavigateClassicAsync(key)
            );
            button.Tag = key;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(18);
            button.Foreground = Brushes.White;
            button.Background = Brushes.Transparent;
            button.VerticalAlignment = VerticalAlignment.Center;
            classicTabs.Children.Add(button);
        }

        Grid.SetColumn(classicTabs, 1);
        header.Children.Add(classicTabs);
        var detailHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(12, 0)
        };
        detailHeader.Children.Add(classicBack);
        detailHeader.Children.Add(classicDetailTitle);
        Grid.SetColumnSpan(detailHeader, 3);
        header.Children.Add(detailHeader);
        classicBack.Click += async (_, _) =>
        {
            // [实现] 回调触发时：按 !resources.ReturnToClassicSearch() 分支处理。
            if (!resources.ReturnToClassicSearch())
            {
                // [实现] 条件成立（!resources.ReturnToClassicSearch()）时：执行 await NavigateClassicAsync(classicSection)。
                await NavigateClassicAsync(classicSection);
            }
        };
        var task = ClassicButton(
            "↓",
            () =>
            {
                // [实现] 回调触发时：执行 TaskCenterClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                TaskCenterClick(
                    null,
                    new RoutedEventArgs()
                );
                return Task.CompletedTask;
            }
        );
        task.Foreground = Brushes.White;
        task.Background = Brushes.Transparent;
        task.HorizontalAlignment = HorizontalAlignment.Right;
        task.Margin = new Thickness(0, 0, 14, 0);
        ToolTip.SetTip(task, "下载任务");
        Grid.SetColumn(task, 2);
        header.Children.Add(task);
        classicHeader = header;
        Grid.SetRowSpan(classicBackground, 2);
        Grid.SetRowSpan(classicTint, 2);
        root.Children.Add(classicBackground);
        root.Children.Add(classicTint);
        classicTitleImage.Margin = new Thickness(12, 0);
        header.Children.Add(classicTitleImage);
        Grid.SetColumn(classicRecovery, 2);
        classicRecovery.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(classicRecovery);
        InitializeLocalAppearance();
        Opened += async (_, _) => await CheckReleaseNotificationsAsync();
        classicBrand = brand;
        MoveControl(BackgroundImage);
        BackgroundImage.IsHitTestVisible = false;
        Grid.SetRowSpan(BackgroundImage, 2);
        root.Children.Add(BackgroundImage);
        root.Children.Add(header);
        var sidebar = new Border
        {
            Background = Brushes.White,
            Child = new ScrollViewer
            {
                Content = classicNavigation
            }
        };
        classicBody.Children.Add(sidebar);
        var surface = new Grid();
        surface.Children.Add(HomePanel);
        surface.Children.Add(ResourcePanel);
        surface.Children.Add(classicPage);
        Grid.SetColumn(surface, 1);
        classicBody.Children.Add(surface);
        Grid.SetRow(classicBody, 1);
        root.Children.Add(classicBody);
        Grid.SetRowSpan(startupLogo, 2);
        root.Children.Add(startupLogo);
        Content = root;
        BuildClassicLaunch();
        ApplyClassicAppearance(appearance);
        resources.DetailNavigationChanged += name =>
        {
            // [实现] 回调触发时：执行 classicTabs.IsVisible = name is null；执行 brand.IsVisible = name is null && appearance.TitleMode != "image"；执行 classicTitleImage.IsVisible = name is null && appearance.TitleMode == "image" && classicTitleIm…。
            classicTabs.IsVisible = name is null;
            brand.IsVisible = name is null && appearance.TitleMode != "image";
            classicTitleImage.IsVisible = name is null && appearance.TitleMode == "image" && classicTitleImage.Source is not null;
            classicBack.IsVisible = name is not null;
            classicDetailTitle.Text = name ?? "";
            classicBody.ColumnDefinitions[0].Width = new GridLength(
                name is null
                    ? 150
                    : 0
            );
        };
        resources.SourceRequested += source =>
        {
            // [实现] 回调触发时：按 source == "curseforge" 分支处理。
            if (source == "curseforge")
            {
                // [实现] 条件成立（source == "curseforge"）时：执行 CurseForgeClick( null, new RoutedEventArgs() )。
                CurseForgeClick(
                    null,
                    new RoutedEventArgs()
                );
            }
            else
            {
                // [实现] 前一条件不成立时：执行 UnifiedSearchClick( null, new RoutedEventArgs() )。
                UnifiedSearchClick(
                    null,
                    new RoutedEventArgs()
                );
            }
        };
        ShowClassicHome();
    }

    // 启动页采用原版左账户/下启动按钮、右主页结构，保留真实账户恢复与进度事件。
    private void BuildClassicLaunch()
    {
        var account = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(18)
        };
        account.Children.Add(
            new TextBlock
            {
                Text = "登录方式",
                Foreground = Brush.Parse("#858585"),
                FontSize = 12
            }
        );
        foreach (var control in new Control[]
        {
            AccountTypeBox,
            AccountText,
            AccountBox,
            OfflineNameBox,
            MicrosoftLoginInfo,
            RememberLoginBox,
            LoginButton,
            LoginHint,
            CopyCodeButton
        }

        )
        {
            // [实现] 对 new Control[] { AccountTypeBox, AccountText, AccountBox, OfflineNameBox, MicrosoftLoginInfo, Re… 中的每一项：执行 MoveControl(control)；执行 control.MinWidth = 0；执行 control.HorizontalAlignment = HorizontalAlignment.Stretch。
            MoveControl(control);
            control.MinWidth = 0;
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            account.Children.Add(control);
        }

        var accountActions = new WrapPanel();
        foreach (var button in new[]
        {
            SkinsButton,
            LogoutButton,
            RemoveAccountButton
        }

        )
        {
            // [实现] 对 new[] { SkinsButton, LogoutButton, RemoveAccountButton } 中的每一项：执行 MoveControl(button)；执行 button.FontSize = 11；执行 button.Padding = new Thickness(6, 5)。
            MoveControl(button);
            button.FontSize = 11;
            button.Padding = new Thickness(6, 5);
            accountActions.Children.Add(button);
        }

        account.Children.Add(accountActions);
        var lower = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8
        };
        MoveControl(ProfileBox);
        ProfileBox.MinWidth = 0;
        ProfileBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        lower.Children.Add(ProfileBox);
        var shortcuts = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*")
        };
        var instances = ClassicButton(
            "版本选择",
            () =>
            {
                // [实现] 回调触发时：执行 InstancesClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                InstancesClick(
                    null,
                    new RoutedEventArgs()
                );
                return Task.CompletedTask;
            }
        );
        shortcuts.Children.Add(instances);
        var settings = ClassicButton(
            "版本设置",
            () =>
            {
                // [实现] 回调触发时：执行 InstancesClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                InstancesClick(
                    null,
                    new RoutedEventArgs()
                );
                return Task.CompletedTask;
            }
        );
        Grid.SetColumn(settings, 1);
        shortcuts.Children.Add(settings);
        lower.Children.Add(shortcuts);
        MoveControl(LaunchButton);
        LaunchButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        LaunchButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        LaunchButton.Height = 56;
        LaunchButton.FontSize = 21;
        LaunchButton.Background = Brush.Parse("#0875D1");
        LaunchButton.Foreground = Brushes.White;
        lower.Children.Add(LaunchButton);
        MoveControl(StopGameButton);
        lower.Children.Add(StopGameButton);
        var left = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            Background = Brushes.White
        };
        left.Children.Add(
            new ScrollViewer
            {
                Content = account
            }
        );
        Grid.SetRow(lower, 1);
        left.Children.Add(lower);
        var right = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 15
        };
        right.Children.Add(
            ClassicCard(
                ProductInfo.Name,
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{ProductInfo.Version}  ·  Apple Silicon",
                            FontSize = 22
                        },
                        new TextBlock
                        {
                            Text = "基于 PCL 2.99.0.0 的导航和页面结构。",
                            TextWrapping = TextWrapping.Wrap
                        },
                        ClassicButton(
                            "查看功能对照",
                            () => NavigateClassicAsync("matrix")
                        )
                    }
                }
            )
        );
        right.Children.Add(releaseNotice);
        right.Children.Add(classicCustomHome);
        var version = new StackPanel
        {
            Spacing = 10
        };
        foreach (var control in new Control[]
        {
            GameVersionText,
            InstallState,
            InstallProgressBar,
            StatusText,
            CancelButton
        }

        )
        {
            // [实现] 对 new Control[] { GameVersionText, InstallState, InstallProgressBar, StatusText, CancelButton } 中的每一项：执行 version.Children.Add( MoveControl(control) )。
            version.Children.Add(
                MoveControl(control)
            );
        }

        GameVersionText.FontSize = 22;
        var actions = new WrapPanel();
        foreach (var button in new[]
        {
            InstallButton,
            VersionButton,
            FabricButton,
            MacSetupButton
        }

        )
        {
            // [实现] 对 new[] { InstallButton, VersionButton, FabricButton, MacSetupButton } 中的每一项：执行 actions.Children.Add( MoveControl(button) )；执行 button.Margin = new Thickness(0, 0, 8, 6)。
            actions.Children.Add(
                MoveControl(button)
            );
            button.Margin = new Thickness(0, 0, 8, 6);
        }

        version.Children.Add(actions);
        right.Children.Add(
            ClassicCard("当前版本", version)
        );
        MoveControl(LogBox);
        LogBox.Height = 160;
        right.Children.Add(
            new Expander
            {
                Header = "运行记录",
                Content = LogBox,
                HorizontalAlignment = HorizontalAlignment.Stretch
            }
        );
        HomePanel.Children.Clear();
        HomePanel.RowDefinitions = RowDefinitions.Parse("*");
        HomePanel.ColumnDefinitions = ColumnDefinitions.Parse("270,*");
        HomePanel.Margin = new Thickness(0);
        HomePanel.Children.Add(left);
        var scroll = new ScrollViewer
        {
            Content = right
        };
        Grid.SetColumn(scroll, 1);
        HomePanel.Children.Add(scroll);
    }

    // 切换页面前同步可见性；所有页面共享同一套业务服务和数据目录。
    private void ShowClassicHome()
    {
        classicSection = classicRoute = "launch";
        classicBody.ColumnDefinitions[0].Width = new GridLength(0);
        HomePanel.IsVisible = true;
        ResourcePanel.IsVisible = false;
        classicPage.IsVisible = false;
        UpdateClassicTabs();
        _ = RefreshClassicHomeAsync();
    }

    // [实现] UpdateClassicTabs 的实现：执行 classicTabs.IsVisible = true；执行 classicBack.IsVisible = false；执行 classicDetailTitle.Text = ""。
    private void UpdateClassicTabs()
    {
        classicTabs.IsVisible = true;
        classicBack.IsVisible = false;
        classicDetailTitle.Text = "";
        foreach (var button in classicTabs.Children.OfType<Button>())
        {
            // [实现] 对 classicTabs.Children.OfType<Button>() 中的每一项：执行 button.IsVisible = !appearance.HiddenRoutes.Contains( button.Tag?.ToString() ?? "" )；执行 button.Background = button.Tag?.ToString() == classicSection ? Brush.Parse("#3790DC") : Brushes…。
            button.IsVisible = !appearance.HiddenRoutes.Contains(
                button.Tag?.ToString() ?? ""
            );
            button.Background = button.Tag?.ToString() == classicSection
                ? Brush.Parse("#3790DC")
                : Brushes.Transparent;
        }
    }

    // 左侧目录直接使用原版标题/图标；开源删节项仍提供可见差异说明，不伪装实现。
    private void BuildClassicNavigation(string section)
    {
        classicNavigation.Children.Clear();
        string area = section == "download"
            ? "Download"
            : section == "settings"
                ? "Setup"
                : "Other";
        string[] routes = section == "download"
            ? [
                "vanilla",
                "mod",
                "modpack",
                "datapack",
                "resourcepack",
                "shader"
            ]
            : section == "settings"
                ? [
                    "setup-launch",
                    "setup-link",
                    "setup-ui",
                    "setup-system"
                ]
                : [
                    "help",
                    "about",
                    "feedback",
                    "vote"
                ];
        // 保留原版布局资料，仅从产品导航中排除已取消的项目；路由与可见菜单保持同序。
        var items = WindowsBaseline["menus"]![area]!.AsArray()
            .Where(
                item => item?["title"]?.GetValue<string>() != "百宝箱"
            )
            .ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            // 依照过滤后的菜单顺序绑定路由；下载页面在社区资源前插入分组标题。
            if (section == "download" && i == 1)
            {
                // [实现] 条件成立（section == "download" && i == 1）时：执行 classicNavigation.Children.Add( new TextBlock { Text = "社区资源", Margin = new Thickness(13, 20, 5…。
                classicNavigation.Children.Add(
                    new TextBlock
                    {
                        Text = "社区资源",
                        Margin = new Thickness(13, 20, 5, 4),
                        FontSize = 12,
                        Foreground = Brushes.Gray
                    }
                );
            }

            var item = items[i]!;
            string route = routes[i];
            if (appearance.HiddenRoutes.Contains(route))
            {
                // [实现] 条件成立（appearance.HiddenRoutes.Contains(route)）时：执行 continue;。
                continue;
            }

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12
            };
            string icon = item["icon"]!.GetValue<string>();
            if (icon.Length > 0)
            {
                // [实现] 条件成立（icon.Length > 0）时：执行 content.Children.Add( new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(icon), Fill = B…。
                content.Children.Add(
                    new Avalonia.Controls.Shapes.Path
                    {
                        Data = Geometry.Parse(icon),
                        Fill = Brush.Parse("#374450"),
                        Width = 21,
                        Height = 21,
                        Stretch = Stretch.Uniform
                    }
                );
            }

            content.Children.Add(
                new TextBlock
                {
                    Text = item["title"]!.GetValue<string>(),
                    VerticalAlignment = VerticalAlignment.Center
                }
            );
            var button = ClassicButton(
                "",
                () => NavigateClassicAsync(route)
            );
            button.Content = content;
            button.Tag = route;
            button.Height = 36;
            button.BorderThickness = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Background = route == classicRoute
                ? Brush.Parse("#E7F2FF")
                : Brushes.Transparent;
            classicNavigation.Children.Add(button);
        }

        if (section == "more")
        {
            // [实现] 条件成立（section == "more"）时：执行 classicNavigation.Children.Add( new TextBlock { Text = "Mac 对照", Margin = new Thickness(13, 20,…；执行 foreach (var(route, label)in new[] { ("matrix", "功能对照"), ("extensions", "Mac 扩展") } ) { var but…。
            classicNavigation.Children.Add(
                new TextBlock
                {
                    Text = "Mac 对照",
                    Margin = new Thickness(13, 20, 5, 4),
                    FontSize = 12,
                    Foreground = Brushes.Gray
                }
            );
            foreach (var(route, label)in new[]
            {
                ("matrix", "功能对照"),
                ("extensions", "Mac 扩展")
            }

            )
            {
                // [实现] 本段流程：准备 var button = ClassicButton( label, () => NavigateClassicAsync(route) )；执行 button.HorizontalAlignment = HorizontalAlignment.Stretch；执行 classicNavigation.Children.Add(button)。
                var button = ClassicButton(
                    label,
                    () => NavigateClassicAsync(route)
                );
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                classicNavigation.Children.Add(button);
            }
        }
    }

    // 导航仅调度操作；网络异常通过页面错误反馈，旧页面异步返回不会覆盖新页面。
    private async Task NavigateClassicAsync(string route)
    {
        if (navigationLifetime.IsStopping)
        {
            return;
        }

        using var navigationActivity = navigationLifetime.Begin();
        if (operation is not null)
        {
            // [实现] 条件成立（operation is not null）时：执行 Log("当前操作结束后可切换页面。")；结束当前过程。
            Log("当前操作结束后可切换页面。");
            return;
        }

        int revision = ++classicPageRevision;
        classicRoute = route;
        if (route == "launch")
        {
            // [实现] 条件成立（route == "launch"）时：执行 ShowClassicHome()；结束当前过程。
            ShowClassicHome();
            return;
        }

        if (route is "download" or "vanilla" or "mod" or "modpack" or "datapack" or "resourcepack" or "shader")
        {
            // [实现] 条件成立（route is "download" or "vanilla" or "mod" or "modpack" or "datapack" or "resourcepack" or "shad…）时：执行 classicSection = "download"。
            classicSection = "download";
        }
        else if (route == "settings" || route.StartsWith("setup-", StringComparison.Ordinal))
        {
            // [实现] 条件成立（route == "settings" || route.StartsWith("setup-", StringComparison.Ordinal)）时：执行 classicSection = "settings"。
            classicSection = "settings";
        }
        else
        {
            // [实现] 前一条件不成立时：执行 classicSection = "more"。
            classicSection = "more";
        }

        classicRoute = route = route switch
        {
            "download" => "vanilla",
            "settings" => "setup-launch",
            "more" => "help",
            _ => route
        };
        UpdateClassicTabs();
        BuildClassicNavigation(classicSection);
        classicBody.ColumnDefinitions[0].Width = new GridLength(150);
        HomePanel.IsVisible = false;
        ResourcePanel.IsVisible = false;
        classicPage.IsVisible = true;
        classicPage.Content = new TextBlock
        {
            Text = "加载中…",
            Margin = new Thickness(24)
        };
        try
        {
            // [实现] 在异常保护范围内：按 route is "mod" or "modpack" or "datapack" or "resourcepack" or "shader" 分支处理。
            if (route is "mod" or "modpack" or "datapack" or "resourcepack" or "shader")
            {
                // [实现] 条件成立（route is "mod" or "modpack" or "datapack" or "resourcepack" or "shader"）时：执行 classicPage.IsVisible = false；执行 ResourcePanel.IsVisible = true；执行 await resources.ShowAsync( route switch { "mod" => ResourceKind.Mod, "shader" => ResourceKind.S…。
                classicPage.IsVisible = false;
                ResourcePanel.IsVisible = true;
                await resources.ShowAsync(
                    route switch
                    {
                        "mod" => Pcl.Core.Contracts.Workspaces.ResourceKind.Mod,
                        "shader" => Pcl.Core.Contracts.Workspaces.ResourceKind.Shader,
                        "modpack" => Pcl.Core.Contracts.Workspaces.ResourceKind.Modpack,
                        "datapack" => Pcl.Core.Contracts.Workspaces.ResourceKind.DataPack,
                        _ => Pcl.Core.Contracts.Workspaces.ResourceKind.ResourcePack
                    }
                );
            }
            else if (route == "vanilla")
            {
                // [实现] 条件成立（route == "vanilla"）时：准备 var view = await BuildClassicVersionsAsync()；按 revision == classicPageRevision 分支处理。
                var view = await BuildClassicVersionsAsync();
                if (revision == classicPageRevision)
                {
                    // [实现] 条件成立（revision == classicPageRevision）时：执行 classicPage.Content = view。
                    classicPage.Content = view;
                }
            }
            else
            {
                // [实现] 前一条件不成立时：执行 classicPage.Content = route switch { "matrix" => BuildFeatureComparison(), "extensions" => Buil…。
                var nextPage = route switch
                {
                    "matrix" => BuildFeatureComparison(),
                    "extensions" => BuildMacExtensions(),
                    "setup-launch" => await BuildWindowsSettingsAsync("PageSetupLaunch"),
                    "setup-link" => await BuildWindowsSettingsAsync("PageSetupLink"),
                    "setup-ui" => await BuildWindowsSettingsAsync("PageSetupUI"),
                    "setup-system" => await BuildWindowsSettingsAsync("PageSetupSystem"),
                    _ => BuildClassicOther(route)
                };
                if (revision == classicPageRevision)
                {
                    classicPage.Content = nextPage;
                }
            }
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：按 revision == classicPageRevision 分支处理。
            if (revision == classicPageRevision)
            {
                classicPage.IsVisible = true;
                classicPage.Content = ClassicCard(
                    "加载失败",
                    new TextBlock
                    {
                        Text = Pcl.Client.WorkspaceErrors.Describe(ex),
                        TextWrapping = TextWrapping.Wrap
                    }
                );
            }
        }

        await UiMotion.FadeIn(
            classicPage.IsVisible
                ? classicPage
                : ResourcePanel
        );
    }

    // 原版游戏下载按发布类型分组，选择后复用已有下载/Java/加载器安装流程。
    private async Task<Control> BuildClassicVersionsAsync()
    {
        var versions = (IReadOnlyList<Pcl.Core.Contracts.Workspaces.MinecraftRelease>)await DesktopApi.CallAsync<Pcl.Core.Contracts.Workspaces.MinecraftRelease[]>("workspace.desktop.versions", null);
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 15
        };
        foreach (var(label, type)in new[]
        {
            ("最新版本", "latest"),
            ("正式版", "release"),
            ("预览版", "snapshot"),
            ("远古版", "old")
        }

        )
        {
            // [实现] 本段流程：准备 var list = new StackPanel { Spacing = 4 }；准备 var selected = type == "latest" ? versions.Take(2) : versions.Where( x => type == "old" ? x.Typ…；遍历 selected 处理 item。
            var list = new StackPanel
            {
                Spacing = 4
            };
            var selected = type == "latest"
                ? versions.Take(2)
                : versions.Where(
                    x => type == "old"
                        ? x.Type.StartsWith("old", StringComparison.Ordinal)
                        : x.Type == type
                );
            foreach (var item in selected)
            {
                // [实现] 对 selected 中的每一项：准备 var button = ClassicButton( item.Id + " · " + item.Type, () => ManageVersionsAsync(item.Id) )；执行 button.HorizontalAlignment = HorizontalAlignment.Stretch；执行 button.HorizontalContentAlignment = HorizontalAlignment.Left。
                var button = ClassicButton(
                    item.Id + "   ·   " + item.Type,
                    () => ManageVersionsAsync(item.Id)
                );
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                list.Children.Add(button);
            }

            panel.Children.Add(
                new Expander
                {
                    Header = $"{label} ({list.Children.Count})",
                    Content = list,
                    IsExpanded = type == "latest",
                    HorizontalAlignment = HorizontalAlignment.Stretch
                }
            );
        }

        panel.Children.Add(
            ClassicButton(
                "手动下载游戏 / 加载器文件",
                () =>
                {
                    // [实现] 回调触发时：执行 DownloadFilesClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                    DownloadFilesClick(
                        null,
                        new RoutedEventArgs()
                    );
                    return Task.CompletedTask;
                }
            )
        );
        return new ScrollViewer
        {
            Content = panel
        };
    }

    // 对照表显示原有证据状态与缺口，不把新壳的可见入口升级为功能验收。
    private Control BuildFeatureComparison()
    {
        var features = ReadEmbeddedJson("feature-matrix.json")["features"]!.AsArray();
        var panel = new StackPanel
        {
            Spacing = 15,
            Margin = new Thickness(24)
        };
        var query = new TextBox
        {
            PlaceholderText = "搜索功能、编号、状态或缺口"
        };
        var onlyGaps = new CheckBox
        {
            Content = "只看未完整验收",
            IsChecked = true
        };
        var results = new StackPanel
        {
            Spacing = 12
        };
        var count = new TextBlock();
        // [实现] Refresh 的实现：执行 results.Children.Clear()；遍历 features 处理 feature；执行 count.Text = $"显示 {results.Children.Count} / {features.Count} 项 · 状态来自现有功能矩阵"。
        void Refresh()
        {
            results.Children.Clear();
            foreach (var feature in features)
            {
                // [实现] 对 features 中的每一项：准备 string state = feature!["status"]?.GetValue<string>() ?? "未标注"；按 onlyGaps.IsChecked == true && state == "已验收" 分支处理；按 !(feature.ToJsonString( new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Enco… 分支处理。
                string state = feature!["status"]?.GetValue<string>() ?? "未标注";
                if (onlyGaps.IsChecked == true && state == "已验收")
                {
                    // [实现] 条件成立（onlyGaps.IsChecked == true && state == "已验收"）时：执行 continue;。
                    continue;
                }

                if (!(feature.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }
                )).Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    // [实现] 条件成立（!(feature.ToJsonString( new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Enco…）时：执行 continue;。
                    continue;
                }

                var text = new StackPanel
                {
                    Spacing = 6
                };
                foreach (var key in new[]
                {
                    "status",
                    "remaining",
                    "ui"
                }

                )
                {
                    // [实现] 对 new[] { "status", "remaining", "ui" } 中的每一项：执行 text.Children.Add( new TextBlock { Text = (key == "ui" ? "现有入口：" : "") + feature[key]?.GetValue…。
                    text.Children.Add(
                        new TextBlock
                        {
                            Text = (key == "ui"
                                ? "现有入口："
                                : "") + feature[key]?.GetValue<string>(),
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12
                        }
                    );
                }

                results.Children.Add(
                    ClassicCard(feature["id"] + "  " + feature["name"], text)
                );
            }

            count.Text = $"显示 {results.Children.Count} / {features.Count} 项 · 状态来自现有功能矩阵";
        }

        query.TextChanged += (_, _) => Refresh();
        onlyGaps.IsCheckedChanged += (_, _) => Refresh();
        panel.Children.Add(
            new TextBlock
            {
                Text = "PCL2 功能对照",
                FontSize = 22
            }
        );
        panel.Children.Add(query);
        panel.Children.Add(onlyGaps);
        panel.Children.Add(count);
        panel.Children.Add(results);
        Refresh();
        return new ScrollViewer
        {
            Content = panel
        };
    }

    // 原版附加入口与 Mac 扩展分开显示，保留已有管理工具，避免因换壳丢失功能。
    private Control BuildMacExtensions()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        foreach (var(label, action)in new (string, EventHandler<RoutedEventArgs>)[]
        {
            ("多源搜索", UnifiedSearchClick),
            ("MC 百科", McmodSearchClick),
            ("CurseForge", CurseForgeClick),
            ("实例管理", InstancesClick),
            ("本地资源", LocalResourcesClick),
            ("整合包管理", PackToolsClick),
            ("下载文件", DownloadFilesClick),
            ("联机", MultiplayerClick),
            ("自定义主页", CustomHomeClick),
            ("游戏文件夹", OpenGameClick),
            ("日志", OpenLogsClick),
            ("Mac 配置", MacSetupClick)
        }

        )
        {
            // [实现] 本段流程：执行 panel.Children.Add( ClassicButton( label, () => { action( null, new RoutedEventArgs() ); return…。
            panel.Children.Add(
                ClassicButton(
                    label,
                    () =>
                    {
                        // [实现] 回调触发时：执行 action( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                        action(
                            null,
                            new RoutedEventArgs()
                        );
                        return Task.CompletedTask;
                    }
                )
            );
        }

        panel.Children.Add(
            ClassicButton(
                "已安装资源",
                async () =>
                {
                    // [实现] 回调触发时：执行 classicPage.IsVisible = false；执行 ResourcePanel.IsVisible = true；执行 await resources.ShowAsync(Pcl.Core.Contracts.Workspaces.ResourceKind.Mod, true)。
                    classicPage.IsVisible = false;
                    ResourcePanel.IsVisible = true;
                    await resources.ShowAsync(Pcl.Core.Contracts.Workspaces.ResourceKind.Mod, true);
                }
            )
        );
        return new ScrollViewer
        {
            Content = panel
        };
    }

    // 构建帮助、关于及服务说明页；已取消或未知路由回到帮助页。
    private Control BuildClassicOther(string route)
    {
        if (route is not ("about" or "help" or "feedback" or "vote"))
        {
            route = "help";
        }

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 15
        };
        string title = route switch
        {
            "about" => "关于与鸣谢",
            "feedback" => "反馈",
            "vote" => "新功能投票",
            _ => "帮助"
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 22
            }
        );
        if (route == "about")
        {
            panel.Children.Clear();
            panel.Children.Add(
                BuildAboutContent()
            );
        }
        else if (route == "help")
        {
            // [实现] 条件成立（route == "help"）时：执行 panel.Children.Add( ClassicCard( "帮助", new StackPanel { Spacing = 12, Children = { ClassicButto…。
            panel.Children.Add(
                ClassicCard(
                    "帮助",
                    new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            ClassicButton(
                                "使用说明",
                                () =>
                                {
                                    // [实现] 回调触发时：执行 SettingsClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                                    SettingsClick(
                                        null,
                                        new RoutedEventArgs()
                                    );
                                    return Task.CompletedTask;
                                }
                            ),
                            ClassicButton(
                                "功能对照与缺口",
                                () => NavigateClassicAsync("matrix")
                            ),
                            ClassicButton(
                                "任务与诊断",
                                () =>
                                {
                                    // [实现] 回调触发时：执行 TaskCenterClick( null, new RoutedEventArgs() )；返回 Task.CompletedTask。
                                    TaskCenterClick(
                                        null,
                                        new RoutedEventArgs()
                                    );
                                    return Task.CompletedTask;
                                }
                            )
                        }
                    }
                )
            );
        }
        else
        {
            // 反馈和投票尚未接入服务，展示现有状态说明。
            panel.Children.Add(
                ClassicCard(
                    title,
                    new TextBlock
                    {
                        Text = "尚未接入 Mac 版专属服务。保留 Windows 原版入口用于对照，当前不提交信息。",
                        TextWrapping = TextWrapping.Wrap
                    }
                )
            );
        }

        return new ScrollViewer
        {
            Content = panel
        };
    }

    // [实现] ClassicCard 的实现：准备 var panel = new StackPanel { Spacing = 12 }；执行 panel.Children.Add( new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = …；执行 panel.Children.Add(content)。
    internal static Border ClassicCard(string title, Control content)
    {
        var panel = new StackPanel
        {
            Spacing = 12
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 13
            }
        );
        panel.Children.Add(content);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#C3D9EF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = panel
        };
    }

    // [实现] ClassicButton 的实现：准备 var button = new Button { Content = label }；执行 button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Log( Diagnosti…；返回 button。
    private Button ClassicButton(string label, Func<Task> action)
    {
        var button = new Button
        {
            Content = label
        };
        button.Click += async (_, _) =>
        {
            // [实现] 回调触发时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：执行 await action()。
                await action();
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 Log( Pcl.Client.WorkspaceErrors.Describe(ex) )。
                Log(
                    Pcl.Client.WorkspaceErrors.Describe(ex)
                );
            }
        };
        return button;
    }
}
