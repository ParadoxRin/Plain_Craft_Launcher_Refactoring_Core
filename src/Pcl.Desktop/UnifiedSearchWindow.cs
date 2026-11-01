using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 资源发现：查询并转换下载平台元数据，按游戏版本和加载器筛选资源，保留来源与可用性信息。
public sealed partial class UnifiedSearchWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly GameProfile profile;
    private readonly Action<string> installed;
    private readonly Action<string> open;
    private readonly TextBox query = new()
    {
        Name = "UnifiedQuery",
        PlaceholderText = "名称 / 百科 ID / 百科链接",
        MinWidth = 210
    };
    private readonly ComboBox kind = new()
    {
        Name = "UnifiedKind",
        ItemsSource = new[]
        {
            "模组",
            "光影",
            "资源包",
            "整合包"
        },
        SelectedIndex = 0
    };
    private readonly ComboBox source = new()
    {
        Name = "UnifiedSource",
        ItemsSource = new[]
        {
            "全部来源",
            "MC 百科",
            "Modrinth",
            "CurseForge",
            "本地"
        },
        SelectedIndex = 0
    };
    private readonly CheckBox allVersions = new()
    {
        Content = "全部游戏版本",
        IsChecked = true
    };
    private readonly TextBox gameVersion = new()
    {
        PlaceholderText = "指定游戏版本",
        Width = 120,
        IsEnabled = false
    };
    private readonly ComboBox loader = new()
    {
        ItemsSource = new[]
        {
            "全部加载器",
            "fabric",
            "forge",
            "neoforge",
            "quilt",
            "liteloader"
        },
        SelectedIndex = 0
    };
    private readonly ComboBox downloadSource = new()
    {
        ItemsSource = new[]
        {
            "全部下载来源",
            "Modrinth",
            "CurseForge"
        },
        SelectedIndex = 0
    };
    private readonly StackPanel downloadChoices = new()
    {
        Spacing = 10,
        IsVisible = false
    };
    private SearchRelease[] availableReleases = [];
    private readonly ComboBox installTarget = new()
    {
        Name = "UnifiedInstallTarget",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private GameProfile InstallTarget => installTarget.SelectedItem as GameProfile ?? profile;

    private readonly CheckBox online = new()
    {
        Name = "UnifiedOnline",
        Content = "搜索在线来源",
        IsChecked = true
    };
    private readonly TextBox key = new()
    {
        PlaceholderText = "CurseForge API Key（可选，仅 Host 内存，30 分钟过期）",
        PasswordChar = '●'
    };
    private readonly ListBox results = new()
    {
        Name = "UnifiedResults"
    };
    private readonly ComboBox versions = new()
    {
        Name = "UnifiedVersions",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock status = new()
    {
        Name = "UnifiedStatus",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock detail = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock releaseDetails = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly List<Button> buttons = [];
    private CancellationTokenSource? operation;
    private SearchPage? session;
    private ResourceKind Kind => kind.SelectedIndex switch
    {
        1 => ResourceKind.Shader,
        2 => ResourceKind.ResourcePack,
        3 => ResourceKind.Modpack,
        _ => ResourceKind.Mod
    };
    public bool Busy => operation is not null;

    public UnifiedSearchWindow(
        IWorkspaceOperations operations,
        GameProfile profile,
        Action<string> installed,
        Action<string> open,
        bool mcmodOnly = false
    )
    {
        source.SelectedIndex = mcmodOnly
            ? 1
            : 0;
        key.IsVisible = !mcmodOnly;
        api = new(operations);
        this.profile = profile;
        this.installed = installed;
        this.open = open;
        NavigationContext = profile.Id + (mcmodOnly
            ? ":mcmod"
            : ":all");
        Title = "统一资源浏览 · " + profile.GameVersion;
        gameVersion.Text = profile.GameVersion;
        Width = 900;
        Height = 720;
        MinWidth = 700;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(24)
        };
        var top = new StackPanel
        {
            Spacing = 10
        };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(
            new TextBlock
            {
                Text = "统一资源浏览",
                FontSize = 26,
                FontWeight = FontWeight.SemiBold
            }
        );
        top.Children.Add(
            new TextBlock
            {
                Text = "Modrinth · 本地 · MC 百科内置索引 · CurseForge",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var search = new WrapPanel
        {
            ItemSpacing = 10
        };
        search.Children.Add(query);
        search.Children.Add(kind);
        search.Children.Add(source);
        search.Children.Add(online);
        search.Children.Add(
            Action(
                "搜索",
                async ct =>
                {
                    session = null;
                    await Search(0, ct);
                }
            )
        );
        top.Children.Add(search);
        var filters = new WrapPanel
        {
            ItemSpacing = 10
        };
        filters.Children.Add(allVersions);
        filters.Children.Add(gameVersion);
        filters.Children.Add(loader);
        top.Children.Add(filters);
        top.Children.Add(key);
        var footer = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var paging = new WrapPanel
        {
            ItemSpacing = 10
        };
        paging.Children.Add(
            Action(
                "上一页",
                async ct =>
                {
                    if (session is not null)
                        await Search(
                            Math.Max(0, session.Page - 1),
                            ct
                        );
                }
            )
        );
        paging.Children.Add(
            Action(
                "下一页",
                async ct =>
                {
                    if (session?.HasNext == true)
                        await Search(session.Page + 1, ct);
                }
            )
        );
        paging.Children.Add(
            Action(
                "重试本页",
                async ct =>
                {
                    if (session?.HasErrors == true)
                        await Search(session.Page, ct, true);
                }
            )
        );
        paging.Children.Add(
            Action(
                "打开本地目录",
                _ =>
                {
                    if (results.SelectedItem is SearchHit { LocalDirectory: not null } x)
                        open(x.LocalDirectory);
                    return Task.CompletedTask;
                }
            )
        );
        footer.Children.Add(paging);
        footer.Children.Add(status);
        var cancel = new Button
        {
            Content = "取消"
        };
        cancel.Click += (_, _) => operation?.Cancel();
        footer.Children.Add(cancel);
        var grid = new Grid
        {
            ColumnDefinitions = new("*,*"),
            ColumnSpacing = 18,
            Margin = new Thickness(0, 14, 0, 0)
        };
        grid.Children.Add(results);
        var side = new StackPanel
        {
            Spacing = 12
        };
        Grid.SetColumn(side, 1);
        grid.Children.Add(
            new ScrollViewer
            {
                Content = side
            }
        );
        Grid.SetColumn(grid.Children[1], 1);
        side.Children.Add(detail);
        side.Children.Add(
            Action("选择版本与下载来源", LoadVersions)
        );
        downloadChoices.Children.Add(
            new TextBlock
            {
                Text = "浏览可选全部版本；安装只列出目标实例兼容文件。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        downloadChoices.Children.Add(
            new TextBlock
            {
                Text = "安装到（游戏版本和加载器随实例）："
            }
        );
        installTarget.ItemsSource = new[]
        {
            profile
        };
        installTarget.SelectedIndex = 0;
        downloadChoices.Children.Add(installTarget);
        downloadChoices.Children.Add(downloadSource);
        downloadChoices.Children.Add(versions);
        downloadChoices.Children.Add(releaseDetails);
        versions.SelectionChanged += (_, _) =>
        {
            releaseDetails.Text = versions.SelectedItem is SearchRelease selected
                ? selected.Source + " · " + selected.FileName + "\n" + string.Join(", ", selected.GameVersions) + " · " + string.Join(", ", selected.Loaders)
                : "";
        };
        downloadChoices.Children.Add(
            Action("安装所选版本（含前置）", Install)
        );
        downloadChoices.Children.Add(
            Action("匹配手动下载文件", MatchDownload)
        );
        side.Children.Add(downloadChoices);
        side.Children.Add(
            Action(
                "百科检索网页",
                _ =>
                {
                    open(
                        "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(query.Text ?? "")
                    );
                    return Task.CompletedTask;
                }
            )
        );
        side.Children.Add(
            Action(
                "百科资料",
                _ =>
                {
                    if (results.SelectedItem is SearchHit { McmodUrl: not null } m)
                        open(m.McmodUrl);
                    return Task.CompletedTask;
                }
            )
        );
        side.Children.Add(
            Action(
                "百科站内下载",
                _ =>
                {
                    if (results.SelectedItem is SearchHit { McmodUrl: not null } m)
                        open(m.DownloadPage!);
                    return Task.CompletedTask;
                }
            )
        );
        side.Children.Add(
            Action("导入已下载 JAR", ImportJar)
        );
        side.Children.Add(
            Action(
                "项目主页",
                _ =>
                {
                    if (results.SelectedItem is SearchHit h)
                    {
                        var url = (versions.SelectedItem as SearchRelease)?.Page ?? h.WebUrl;
                        if (url is not null)
                            open(url);
                    }

                    return Task.CompletedTask;
                }
            )
        );
        root.Children.Add(grid);
        Content = root;
        results.SelectionChanged += (_, _) =>
        {
            downloadChoices.IsVisible = false;
            availableReleases = [];
            versions.ItemsSource = null;
            if (results.SelectedItem is SearchHit hit)
            {
                detail.Text = hit.Title + "\n" + hit.Description + "\n" + string.Join(" · ", hit.Sources);
            }
        };
        downloadSource.SelectionChanged += (_, _) => FilterDownloads();
        installTarget.SelectionChanged += (_, _) =>
        {
            availableReleases = [];
            versions.ItemsSource = null;
            status.Text = "目标实例已变更，请重新点击“选择版本与下载来源”。";
        };
        void Reset()
        {
            source.IsVisible = true;
            if (Kind != ResourceKind.Mod && source.SelectedIndex == 1)
            {
                source.SelectedIndex = 0;
            }

            key.IsVisible = source.SelectedIndex is 0 or 3;
            downloadChoices.IsVisible = false;
            availableReleases = [];
            foreach (var b in buttons)
                if (b.Content?.ToString()is "百科检索网页" or "百科资料" or "百科站内下载" or "导入已下载 JAR")
                    b.IsVisible = Kind == ResourceKind.Mod;
            results.ItemsSource = null;
            versions.ItemsSource = null;
            detail.Text = "";
            status.Text = "";
            session = null;
        }

        kind.SelectionChanged += (_, _) => Reset();
        source.SelectionChanged += (_, _) => Reset();
        query.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                Reset();
        };
        key.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                secretHandle = null;
                Reset();
            }
        };
        online.IsCheckedChanged += (_, _) => Reset();
        allVersions.IsCheckedChanged += (_, _) =>
        {
            gameVersion.IsEnabled = allVersions.IsChecked != true;
            Reset();
        };
        gameVersion.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Reset();
            }
        };
        loader.SelectionChanged += (_, _) => Reset();
        ViewLifetime.OnStop(
            () =>
            {
                key.Text = "";
                return Task.CompletedTask;
            }
        );
    }

    private Button Action(string text, Func<CancellationToken, Task> action)
    {
        var b = new Button
        {
            Content = text
        };
        buttons.Add(b);
        b.Click += async (_, _) => await Run(action);
        return b;
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || ViewClosing)
            return;
        operation = new();
        using var viewActivity = ViewLifetime.Begin(operation.Cancel);
        foreach (var b in buttons)
            b.IsEnabled = false;
        results.IsEnabled = query.IsEnabled = kind.IsEnabled = key.IsEnabled = online.IsEnabled = source.IsEnabled = false;
        allVersions.IsEnabled = gameVersion.IsEnabled = loader.IsEnabled = downloadSource.IsEnabled = installTarget.IsEnabled = false;
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = "已取消";
        }
        catch (Exception ex)
        {
            status.Text = WorkspaceErrors.Describe(ex);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            foreach (var b in buttons)
                b.IsEnabled = true;
            results.IsEnabled = query.IsEnabled = kind.IsEnabled = key.IsEnabled = online.IsEnabled = source.IsEnabled = true;
            allVersions.IsEnabled = loader.IsEnabled = downloadSource.IsEnabled = installTarget.IsEnabled = true;
            gameVersion.IsEnabled = allVersions.IsChecked != true;
        }
    }

    // 查询与分页交给 Host；界面仅保留快照和选择句柄，关闭时等待取消完成。
    private string? secretHandle;
    private async Task<string?> SecretAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key.Text))
        {
            return null;
        }

        return secretHandle ??= await api.SecretAsync(key.Text, ct);
    }

    private async Task Search(int page, CancellationToken ct, bool retry = false)
    {
        status.Text = "搜索中…";
        session = await api.CallAsync<SearchPage>(
            "workspace.search.page",
            profile.Id,
            new
            {
                session = session?.Session,
                page,
                retry,
                query = query.Text ?? "",
                kind = (int)Kind,
                provider = source.SelectedIndex switch
                {
                    1 => "mcmod",
                    2 => "modrinth",
                    3 => "curseforge",
                    4 => "local",
                    _ => "all"
                },
                gameVersion = allVersions.IsChecked == true
                    ? ""
                    : (gameVersion.Text ?? "").Trim(),
                loader = loader.SelectedIndex == 0
                    ? ""
                    : loader.SelectedItem?.ToString() ?? "",
                online = online.IsChecked == true,
                secretHandle = await SecretAsync(ct)
            },
            ct
        );
        versions.ItemsSource = null;
        detail.Text = "";
        results.ItemsSource = session.Hits;
        status.Text = $"第 {session.Page + 1} 页 · {session.Hits.Length} 项 · 合并 {session.FilteredDuplicates} 个重复项目\n" + string.Join(" · ", session.SourceStatus) + (session.HasErrors
            ? "\n部分来源失败，可重试本页。"
            : "");
    }

    private async Task LoadVersions(CancellationToken ct)
    {
        versions.ItemsSource = null;
        if (results.SelectedItem is not SearchHit hit)
        {
            return;
        }

        detail.Text = hit.Title + "\n" + hit.Description + "\n" + string.Join(" + ", hit.Sources);
        if (online.IsChecked != true)
        {
            status.Text = "本地索引；启用在线来源后查询兼容版本。";
            return;
        }

        var target = InstallTarget;
        var instances = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null, ct: ct);
        installTarget.ItemsSource = instances.Items.Select(
            x => x.Profile
        )
            .ToArray();
        installTarget.SelectedItem = instances.Items.Select(
            x => x.Profile
        )
            .FirstOrDefault(
                x => x.Id == target.Id
            ) ?? profile;
        var found = await api.CallAsync<SearchVersions>(
            "workspace.search.versions",
            InstallTarget.Id,
            new
            {
                selection = hit.Selection,
                secretHandle = await SecretAsync(ct)
            },
            ct
        );
        availableReleases = found.Releases;
        downloadChoices.IsVisible = true;
        FilterDownloads();
        status.Text = Kind == ResourceKind.Mod && InstallTarget.Loader == "vanilla"
            ? "原版实例不能直接安装模组；请选择加载器实例，或先在组件管理安装加载器。"
            : $"找到 {found.Releases.Length} 个兼容版本。" + found.Message;
    }

    // 这里只过滤 Host 已确认的候选快照，不重新推断兼容性或构造下载地址。
    private void FilterDownloads()
    {
        var selected = downloadSource.SelectedIndex == 0
            ? null
            : downloadSource.SelectedItem?.ToString();
        versions.ItemsSource = availableReleases.Where(
            x => selected is null || x.Source == selected
        )
            .ToArray();
        versions.SelectedIndex = 0;
    }

    // 本地导入同样进入 Host，类型、加载器和 JAR 元数据由执行层检查。
    public async Task ImportFileAsync(string path, CancellationToken ct = default)
    {
        await api.CallAsync<System.Text.Json.JsonElement>(
            "workspace.search.import",
            profile.Id,
            new
            {
                path
            },
            ct
        );
        status.Text = "已导入并检查";
    }

    private async Task ImportJar(CancellationToken ct)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new()
            {
                Title = "导入已下载的模组",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("JAR 模组")
                    {
                        Patterns = [
                            "*.jar"
                        ]
                    }
                ]
            }
        );
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            await ImportFileAsync(path, ct);
    }

    private async Task MatchDownload(CancellationToken ct)
    {
        var selected = versions.SelectedItem as SearchRelease ?? throw new InvalidOperationException("请选择文件版本。");
        if (!selected.Manual)
        {
            throw new InvalidOperationException("此文件允许自动下载。");
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new()
            {
                Title = "选择已从作者页面下载的文件",
                AllowMultiple = false
            }
        );
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        await api.CallAsync<bool>(
            "workspace.search.match",
            InstallTarget.Id,
            new
            {
                selection = selected.Selection,
                path
            },
            ct
        );
        status.Text = "文件已按来源记录校验，可点击安装。";
    }

    private async Task Install(CancellationToken ct)
    {
        var selected = versions.SelectedItem as SearchRelease ?? throw new InvalidOperationException("请选择兼容版本。");
        var secret = await SecretAsync(ct);
        var result = await api.CallAsync<SearchInstalled>(
            "workspace.search.install",
            InstallTarget.Id,
            new
            {
                selection = selected.Selection,
                secretHandle = secret
            },
            ct,
            new Progress<Pcl.Core.Contracts.Installation.VersionInstallProgress>(
                x => status.Text = x.Stage
            )
        );
        if (result.Pack is { } pack)
        {
            var optional = await PackSelectionWindow.SelectAsync(this, pack.Plan, false, ct);
            var instance = await api.CallAsync<GameProfile>(
                "workspace.packs.install",
                null,
                new
                {
                    handle = pack.Handle,
                    options = new PackImportOptions(SelectedOptionalFiles: optional),
                    secretHandle = secret
                },
                ct
            );
            installed(instance.Id);
            status.Text = "已安装：" + instance.Name;
        }
        else
        {
            status.Text = $"已安装并校验 · {result.Files} 个文件\n" + string.Join("\n", result.Warnings);
        }
    }
}
