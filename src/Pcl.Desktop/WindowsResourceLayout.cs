using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 原版下载目录布局：完整宽度的搜索/结果页进入独立详情页，复用资源安装与校验服务。
public sealed partial class ResourceBrowser
{
    private bool ClassicEnabled => !Program.Arguments.Contains("--modern-layout");

    private readonly Grid classicRoot = new()
    {
        RowDefinitions = RowDefinitions.Parse("*,Auto"),
        Margin = new Thickness(24, 20)
    };
    private readonly Grid classicSearch = new()
    {
        RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
        RowSpacing = 14
    };
    private readonly StackPanel classicDetail = new()
    {
        Spacing = 14
    };
    private readonly ScrollViewer classicDetailScroll = new()
    {
        IsVisible = false
    };
    private Expander? classicFileActions;
    private readonly StackPanel classicVersionGroups = new()
    {
        Spacing = 12
    };
    private readonly TextBox browseVersion = new()
    {
        PlaceholderText = "全部（也可自行输入）",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox browseLoader = new()
    {
        ItemsSource = new[]
        {
            "全部加载器",
            "Fabric",
            "Forge",
            "NeoForge",
            "Quilt"
        },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox browseSource = new()
    {
        ItemsSource = new[]
        {
            "全部",
            "CurseForge",
            "Modrinth"
        },
        SelectedIndex = 2,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock classicSearchTitle = new()
    {
        FontWeight = FontWeight.SemiBold,
        FontSize = 13
    };
    private readonly List<Bitmap> classicIcons = new();
    private readonly CancellationTokenSource iconLifetime = new();
    private readonly HashSet<Task> iconTasks = new();
    // Optional images are owned by the view and cancelled at disposal; business download work stays in Host.
    private async void StartIcon(Image image, string handle)
    {
        var task = LoadClassicIconAsync(image, handle);
        iconTasks.Add(task);
        try
        {
            await task;
        }
        catch (Exception)
        { /* An unavailable thumbnail must not hide searchable resources. */
        }
        finally
        {
            iconTasks.Remove(task);
        }
    }

    private string BrowseVersion => (browseVersion.Text ?? "").Trim();
    private string BrowseLoader => browseLoader.SelectedIndex == 0
        ? ""
        : browseLoader.SelectedItem?.ToString()?.ToLowerInvariant() ?? "";

    public event Action<string?>? DetailNavigationChanged;
    public event Action<string>? SourceRequested;
    internal bool ClassicActionsVisible => classicFileActions?.IsVisible == true && selectedRelease is not null;
    internal int ClassicProjectCount => projects.ItemCount;
    internal int ClassicVersionGroupCount => classicVersionGroups.Children.Count;

    // 构造时移动现有控件，保留搜索/安装/取消事件，视图间切换不创建第二个资源浏览器。
    private void InitializeClassicResources()
    {
        if (!ClassicEnabled)
        {
            return;
        }

        var filters = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("44,*,44,180"),
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto"),
            RowSpacing = 10,
            ColumnSpacing = 10
        };
        void Cell(Control control, int row, int column)
        {
            MainWindow.MoveControl(control);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            filters.Children.Add(control);
        }

        query.PlaceholderText = "";
        query.HorizontalAlignment = HorizontalAlignment.Stretch;
        Cell(
            new TextBlock
            {
                Text = "名称",
                VerticalAlignment = VerticalAlignment.Center
            },
            0,
            0
        );
        Cell(query, 0, 1);
        Cell(
            new TextBlock
            {
                Text = "来源",
                VerticalAlignment = VerticalAlignment.Center
            },
            0,
            2
        );
        Cell(browseSource, 0, 3);
        Cell(
            new TextBlock
            {
                Text = "版本",
                VerticalAlignment = VerticalAlignment.Center
            },
            1,
            0
        );
        Cell(browseVersion, 1, 1);
        Cell(
            new TextBlock
            {
                Text = "类型",
                VerticalAlignment = VerticalAlignment.Center
            },
            1,
            2
        );
        category.MinWidth = 0;
        category.HorizontalAlignment = HorizontalAlignment.Stretch;
        Cell(category, 1, 3);
        var search = new Button
        {
            Content = "搜索",
            MinWidth = 130
        };
        search.Click += async (_, _) => await ClassicSearchAsync();
        var reset = new Button
        {
            Content = "重置条件",
            MinWidth = 130
        };
        reset.Click += async (_, _) =>
        {
            query.Text = "";
            browseVersion.Text = "";
            browseLoader.SelectedIndex = 0;
            browseSource.SelectedIndex = 2;
            category.SelectedIndex = 0;
            sort.SelectedIndex = 0;
            await ClassicSearchAsync();
        };
        var actions = new WrapPanel();
        foreach (var control in new Control[]
        {
            search,
            reset,
            support,
            importPackButton,
            optionalFiles
        }

        )
        {
            MainWindow.MoveControl(control);
            control.Margin = new Thickness(0, 0, 10, 4);
            actions.Children.Add(control);
        }

        Grid.SetRow(actions, 2);
        Grid.SetColumn(actions, 1);
        Grid.SetColumnSpan(actions, 3);
        filters.Children.Add(actions);
        var searchPanel = new StackPanel
        {
            Spacing = 12
        };
        searchPanel.Children.Add(classicSearchTitle);
        searchPanel.Children.Add(filters);
        var advanced = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        advanced.Children.Add(browseLoader);
        MainWindow.MoveControl(sort);
        advanced.Children.Add(sort);
        actions.Children.Add(
            new Expander
            {
                Header = "加载器 / 排序",
                Content = advanced,
                FontSize = 11,
                Padding = new Thickness(0)
            }
        );
        var searchCard = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brush.Parse("#BED4E9"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = searchPanel
        };
        classicSearch.Children.Add(searchCard);
        MainWindow.MoveControl(projects);
        projects.BorderThickness = new Thickness(0);
        var resultCard = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = projects
        };
        Grid.SetRow(resultCard, 1);
        classicSearch.Children.Add(resultCard);
        MainWindow.MoveControl(paging);
        Grid.SetRow(paging, 2);
        classicSearch.Children.Add(paging);
        classicRoot.Children.Add(classicSearch);
        classicDetailScroll.Content = classicDetail;
        classicRoot.Children.Add(classicDetailScroll);
        var feedback = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0)
        };
        status.Foreground = Brush.Parse("#667786");
        status.FontSize = 12;
        status.MinHeight = 18;
        compatibility.Foreground = Brush.Parse("#986423");
        foreach (var control in new Control[]
        {
            status,
            cancel,
            compatibility,
            manageBar
        }

        )
        {
            feedback.Children.Add(
                MainWindow.MoveControl(control)
            );
        }

        Grid.SetRow(feedback, 1);
        classicRoot.Children.Add(feedback);
        mutating.AddRange(
            new Control[]
            {
                search,
                reset,
                browseVersion,
                browseSource,
                browseLoader
            }
        );
        Content = classicRoot;
    }

    // 其它数据源沿用已实现的多源/CF 工作台，明确展示迁移边界，不冒充 Modrinth 结果。
    private async Task ClassicSearchAsync()
    {
        if (busy)
        {
            return;
        }

        offset = 0;
        await SearchAsync();
    }

    // 搜索和翻页共用来源路由，防止来源已切换却继续显示 Modrinth 分页结果。
    private bool RouteClassicSource()
    {
        if (!ClassicEnabled || browseSource.SelectedIndex == 2)
        {
            return false;
        }

        projects.ItemsSource = null;
        previous.IsEnabled = false;
        next.IsEnabled = false;
        page.Text = "";
        status.Text = "此来源暂通过现有搜索工作台打开；原版页面内的多源合并布局待对齐。";
        SourceRequested?.Invoke(
            browseSource.SelectedIndex == 1
                ? "curseforge"
                : "all"
        );
        return true;
    }

    private void ResetClassicResourceLayout(bool installed)
    {
        if (!ClassicEnabled)
        {
            return;
        }

        ReturnToClassicSearch();
        classicSearchTitle.Text = installed
            ? "已安装资源"
            : "搜索" + (kind switch
            {
                ResourceKind.Mod => " Mod",
                ResourceKind.Shader => "光影包",
                ResourceKind.ResourcePack => "资源包",
                ResourceKind.DataPack => "数据包",
                _ => "整合包"
            });
        browseLoader.IsVisible = kind == ResourceKind.Mod;
        classicSearch.Children[0].IsVisible = !installed;
    }

    public bool ReturnToClassicSearch()
    {
        bool changed = classicDetailScroll.IsVisible;
        classicDetailScroll.IsVisible = false;
        classicSearch.IsVisible = true;
        DetailNavigationChanged?.Invoke(null);
        return changed;
    }

    // 项目行使用原站图标、名称、说明、作者与下载量；图标失败保留首字占位，不影响搜索。
    private ListBoxItem CreateClassicProjectRow(ResourceProject project)
    {
        var row = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("50,*"),
            ColumnSpacing = 10,
            Margin = new Thickness(4, 7)
        };
        var icon = new Image
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.UniformToFill
        };
        var fallback = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(7),
            Background = Brush.Parse("#D2E9FA"),
            Child = new TextBlock
            {
                Text = project.Title.Length > 0
                    ? project.Title[..1]
                    : "?",
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        row.Children.Add(fallback);
        row.Children.Add(icon);
        var text = new StackPanel
        {
            Spacing = 4
        };
        text.Children.Add(
            new TextBlock
            {
                Text = project.Title,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        );
        text.Children.Add(
            new TextBlock
            {
                Text = project.Description,
                FontSize = 12,
                Foreground = Brush.Parse("#8A8A8A"),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        );
        text.Children.Add(
            new TextBlock
            {
                Text = $"{project.Author}    ↓ {project.Downloads:N0}    ◎ Modrinth",
                FontSize = 12,
                Foreground = Brush.Parse("#8A8A8A")
            }
        );
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (project.IconHandle is not null)
        {
            StartIcon(icon, project.IconHandle);
        }

        return new ListBoxItem
        {
            Content = row,
            Tag = project,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(2)
        };
    }

    // 仅加载 Modrinth HTTPS 图标，限定长度和生命周期；窗口关闭后停止请求并释放位图。
    private async Task LoadClassicIconAsync(Image image, string url)
    {
        try
        {
            var bytes = await Api.ReadBinaryAsync(
                "workspace.catalog.icon",
                null,
                new
                {
                    selection = url
                },
                iconLifetime.Token
            );
            if (bytes is null)
            {
                return;
            }

            using var data = new MemoryStream(bytes);
            data.Position = 0;
            var bitmap = Bitmap.DecodeToWidth(data, 96);
            if (iconLifetime.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            classicIcons.Add(bitmap);
            image.Source = bitmap;
        }
        catch (Exception ex)when (ex is HttpRequestException or IOException or OperationCanceledException or ArgumentException or Pcl.Core.Contracts.Operations.OperationRejectedException)
        {
        }
    }

    // 独立详情页按游戏版本和加载器分组，选择文件后沿用现有安装、另存为和更新日志动作。
    private void ShowClassicDetail(ResourceProject project, IReadOnlyList<ResourceRelease> versions)
    {
        if (!ClassicEnabled)
        {
            return;
        }

        classicDetail.Children.Clear();
        classicVersionGroups.Children.Clear();
        classicSearch.IsVisible = false;
        classicDetailScroll.IsVisible = true;
        DetailNavigationChanged?.Invoke(project.Title);
        var overview = new StackPanel
        {
            Spacing = 12
        };
        var projectRow = CreateClassicProjectRow(project);
        var projectView = (Control)projectRow.Content!;
        projectRow.Content = null;
        overview.Children.Add(projectView);
        var links = new WrapPanel();
        var web = new Button
        {
            Content = "转到 Modrinth",
            Margin = new Thickness(0, 0, 10, 0)
        };
        web.Click += (_, _) => open(project.WebUrl);
        links.Children.Add(web);
        var wiki = new Button
        {
            Content = "转到 MC 百科",
            Margin = new Thickness(0, 0, 10, 0)
        };
        wiki.Click += (_, _) => open(project.WikiUrl);
        if (kind == ResourceKind.Mod)
        {
            links.Children.Add(wiki);
        }

        var copy = new Button
        {
            Content = "复制名称"
        };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, project.Title);
            }
        };
        links.Children.Add(copy);
        overview.Children.Add(links);
        classicDetail.Children.Add(
            new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16),
                Child = overview
            }
        );
        var filterButtons = new WrapPanel();
        void PopulateGroups(string? gameVersion)
        {
            classicVersionGroups.Children.Clear();
            var entries = versions.SelectMany(
                release => release.GameVersions.Where(
                    v => gameVersion is null || v == gameVersion || v.StartsWith(gameVersion + ".", StringComparison.Ordinal)
                )
                    .Select(
                        version => (Version: version, Release: release)
                    )
            );
            var groups = entries.OrderByDescending(
                x => NumericVersion(x.Version)
            )
                .ThenByDescending(
                    x => x.Version,
                    StringComparer.Ordinal
                )
                .GroupBy(
                    x => (kind == ResourceKind.Mod
                        ? string.Join(" / ", x.Release.Loaders) + " "
                        : "") + x.Version
                );
            foreach (var group in groups)
            {
                var files = new StackPanel
                {
                    Spacing = 4,
                    Margin = new Thickness(10)
                };
                foreach (var entry in group.DistinctBy(
                    x => x.Release.Id
                ))
                {
                    var release = entry.Release;
                    var text = new StackPanel
                    {
                        Spacing = 4
                    };
                    text.Children.Add(
                        new TextBlock
                        {
                            Text = release.Name,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    );
                    text.Children.Add(
                        new TextBlock
                        {
                            Text = $"{release.File.Name} · {release.Published:yyyy-MM-dd} · {release.Channel}",
                            FontSize = 11,
                            Foreground = Brushes.Gray,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    );
                    var select = new Button
                    {
                        Content = text,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0)
                    };
                    select.Click += (_, _) =>
                    {
                        releases.SelectedItem = release;
                        status.Text = "已选择：" + release.File.Name;
                        install.IsEnabled = !busy;
                        if (classicFileActions is not null)
                        {
                            classicFileActions.IsVisible = true;
                            classicFileActions.IsExpanded = true;
                            classicDetailScroll.ScrollToHome();
                        }
                    };
                    files.Children.Add(select);
                }

                classicVersionGroups.Children.Add(
                    new Expander
                    {
                        Background = Brushes.White,
                        Header = group.Key.Replace("neoforge", "NeoForge")
                            .Replace("fabric", "Fabric")
                            .Replace("forge", "Forge")
                            .Replace("quilt", "Quilt"),
                        Content = files,
                        IsExpanded = classicVersionGroups.Children.Count == 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    }
                );
            }
        }

        foreach (string? version in new string? []
        {
            null
        }.Concat(
            versions.SelectMany(
                x => x.GameVersions
            )
                .Select(
                    x => string.Join(
                        ".",
                        x.Split('-')[0].Split('.')
                            .Take(2)
                    )
                )
                .Distinct()
                .OrderByDescending(NumericVersion)
        ))
        {
            var filter = new Button
            {
                Content = version ?? "全部",
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(8, 4),
                BorderThickness = new Thickness(0),
                Foreground = Brush.Parse("#0875D1"),
                CornerRadius = new CornerRadius(16)
            };
            filter.Click += (_, _) => PopulateGroups(version);
            filterButtons.Children.Add(filter);
        }

        classicDetail.Children.Add(
            new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = filterButtons
            }
        );
        classicDetail.Children.Add(classicVersionGroups);
        // 操作固定在详情顶部，避免大量版本组使安装和另存为按钮落到滚动末尾。
        MainWindow.MoveControl(detail);
        detail.Margin = new Thickness(12);
        description.Foreground = Brush.Parse("#65717E");
        title.IsVisible = false;
        description.IsVisible = false;
        releases.IsVisible = true;
        classicFileActions = new Expander
        {
            Header = "下载 / 安装到当前实例 · " + TargetVersion + " / " + TargetLoader,
            Content = detail,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        classicDetail.Children.Insert(1, classicFileActions);
        PopulateGroups(null);
        classicDetailScroll.ScrollToHome();
    }

    // 数值排序仅控制目录顺序，不参与加载器依赖版本比较或安装决策。
    private static Version NumericVersion(string value)
    {
        return Version.TryParse(
            value.Split('-')[0],
            out var version
        )
            ? version
            : new Version(0, 0);
    }

    // 实测入口只驱动真实目录服务；不注入演示数据。
    internal async Task ClassicSearchForSmokeAsync(string term)
    {
        query.Text = term;
        await ClassicSearchAsync();
    }

    internal async Task ClassicOpenFirstForSmokeAsync()
    {
        suppressSelection = true;
        projects.SelectedIndex = 0;
        suppressSelection = false;
        await SelectAsync();
    }

    // 驱动真正的文件行点击，检验后续操作面板的选中版本与当前实例保护。
    internal async Task ClassicSelectFileForSmokeAsync()
    {
        var group = (Expander)classicVersionGroups.Children[0];
        var files = (StackPanel)group.Content!;
        ((Button)files.Children[0]).RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)
        );
        if (kind == ResourceKind.Mod && selectedRelease is { } release && !release.GameVersions.Contains(TargetVersion))
        {
            await InstallSelectedAsync();
            if (status.Text?.Contains("不匹配当前实例", StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException("跨版本文件没有被安装前检查拒绝。");
            }
        }
    }

    private void DisposeClassicResources()
    {
        iconLifetime.Cancel();
        foreach (var image in classicIcons)
        {
            image.Dispose();
        }

        classicIcons.Clear();
        iconLifetime.Dispose();
    }
}
