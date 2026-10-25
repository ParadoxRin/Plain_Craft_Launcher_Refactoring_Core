using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Client;

namespace Pcl.Desktop;
// 资源发现：查询并转换下载平台元数据，按游戏版本和加载器筛选资源，保留来源与可用性信息。
public sealed partial class ResourceBrowser : UserControl, IDisposable
{
    private readonly Func<WorkspaceApi> workspace;
    private WorkspaceApi? workspaceApi;
    private WorkspaceApi Api => workspaceApi ??= workspace();

    private readonly Func<Func<CancellationToken, Task>, Task> run;
    private readonly Func<CancellationToken, Task<GameProfile>> ensureFabric;
    private readonly Func<GameProfile> profile;
    private readonly Func<ResourceRelease, bool, CancellationToken, Task<GameProfile>> installPack;
    private readonly Func<string, bool, CancellationToken, Task<GameProfile>> importPack;
    private readonly CheckBox optionalFiles = new()
    {
        Content = "包含可选文件",
        IsVisible = false
    };
    private readonly Button importPackButton = new()
    {
        Content = "导入整合包",
        IsVisible = false
    };
    private string TargetLoader => profile()
        .Loader;
    private string TargetVersion => profile()
        .GameVersion;

    private readonly Action<string> open;
    private readonly TextBlock heading = new()
    {
        FontSize = 28,
        FontWeight = FontWeight.SemiBold
    };
    private readonly TextBox query = new()
    {
        PlaceholderText = "搜索名称",
        MinWidth = 140
    };
    private readonly ComboBox sort = new()
    {
        ItemsSource = new[]
        {
            "相关度",
            "下载量",
            "最近更新"
        },
        SelectedIndex = 0,
        MinWidth = 110
    };
    private readonly ComboBox category = new()
    {
        ItemsSource = new[]
        {
            "全部分类"
        },
        SelectedIndex = 0,
        MinWidth = 140
    };
    private readonly ListBox projects = new()
    {
        Background = Brushes.Transparent
    };
    private readonly ComboBox releases = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private Button? webButton;
    private readonly TextBlock title = new()
    {
        FontSize = 20,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock description = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush.Parse("#AAB7CB")
    };
    private readonly TextBlock compatibility = new()
    {
        Text = "",
        Foreground = Brush.Parse("#DDBA80"),
        FontSize = 12,
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 34,
        Foreground = Brush.Parse("#A8BEFF")
    };
    private readonly TextBlock page = new()
    {
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button install = new()
    {
        Content = "安装（含前置）",
        IsEnabled = false
    };
    private readonly Button previous = new()
    {
        Content = "上一页",
        IsEnabled = false
    };
    private readonly Button next = new()
    {
        Content = "下一页",
        IsEnabled = false
    };
    private readonly StackPanel searchBar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10
    };
    private readonly StackPanel detail = new()
    {
        Spacing = 16,
        Margin = new Thickness(20)
    };
    private readonly StackPanel manageBar = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        IsVisible = false
    };
    private readonly StackPanel paging = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12
    };
    private readonly Button cancel = new()
    {
        Content = "取消",
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly Button support = new()
    {
        Content = "Iris 支持（实验）",
        IsVisible = false
    };
    private ResourceKind kind;
    private bool management;
    private bool suppressSelection;
    private int offset;
    private int total;
    private int selectionRevision;
    private bool busy;
    private ResourceRelease? selectedRelease;
    private ResourceProject? selectedProject;
    private ManagedResource? selectedManaged;
    private readonly List<Control> mutating = new();
    public ResourceBrowser(
        Func<Func<CancellationToken, Task>, Task> run,
        Func<CancellationToken, Task<GameProfile>> ensureFabric,
        Func<GameProfile> profile,
        Action<string> open,
        Action cancelOperation,
        Func<ResourceRelease, bool, CancellationToken, Task<GameProfile>> installPack,
        Func<string, bool, CancellationToken, Task<GameProfile>> importPack,
        Func<WorkspaceApi> workspace
    )
    {
        this.workspace = workspace;
        this.installPack = installPack;
        this.importPack = importPack;
        this.run = run;
        this.ensureFabric = ensureFabric;
        this.profile = profile;
        this.open = open;
        var layout = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(32, 28, 32, 20),
            RowSpacing = 16
        };
        layout.Children.Add(heading);
        var search = new Button
        {
            Content = "搜索"
        };
        search.Click += async (_, _) =>
        {
            offset = 0;
            await SearchAsync();
        };
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !busy)
            {
                offset = 0;
                await (ClassicEnabled
                    ? ClassicSearchAsync()
                    : SearchAsync());
            }
        };
        searchBar.Children.Add(query);
        searchBar.Children.Add(sort);
        searchBar.Children.Add(search);
        searchBar.Children.Add(support);
        searchBar.Children.Add(importPackButton);
        searchBar.Children.Add(optionalFiles);
        importPackButton.Click += async (_, _) => await Execute(
            async ct =>
            {
                var owner = TopLevel.GetTopLevel(this);
                if (owner is null)
                    return;
                var files = await owner.StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title = "导入整合包",
                        FileTypeFilter = new[]
                        {
                            new FilePickerFileType("Modrinth 整合包")
                            {
                                Patterns = new[]
                                {
                                    "*.mrpack"
                                }
                            }
                        }
                    }
                );
                if (files.Count == 0)
                    return;
                var result = await importPack(
                    files[0].TryGetLocalPath() ?? throw new IOException("文件没有本地路径。"),
                    optionalFiles.IsChecked == true,
                    ct
                );
                status.Text = "已安装 · " + result.Name;
            }
        );
        var toolbar = new StackPanel
        {
            Spacing = 8
        };
        toolbar.Children.Add(searchBar);
        toolbar.Children.Add(category);
        toolbar.Children.Add(manageBar);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);
        var columns = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*"),
            ColumnSpacing = 14
        };
        columns.Children.Add(
            Card(projects)
        );
        var detailScroll = new ScrollViewer
        {
            Content = detail,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var detailCard = Card(detailScroll);
        Grid.SetColumn(detailCard, 1);
        columns.Children.Add(detailCard);
        Grid.SetRow(columns, 2);
        layout.Children.Add(columns);
        detail.Children.Add(title);
        detail.Children.Add(description);
        detail.Children.Add(releases);
        detail.Children.Add(install);
        var web = webButton = new Button
        {
            Content = "项目主页"
        };
        web.Click += (_, _) =>
        {
            if (selectedProject is not null)
                open(selectedProject.WebUrl);
        };
        detail.Children.Add(web);
        var more = new WrapPanel();
        Button DetailAction(string text, Func<CancellationToken, Task> action)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(0, 0, 8, 6)
            };
            b.Click += async (_, _) => await Execute(action);
            more.Children.Add(b);
            mutating.Add(b);
            return b;
        }

        DetailAction(
            "详情",
            async ct =>
            {
                if (selectedProject is not null)
                    await TextDialog(
                        selectedProject.Title,
                        await Api.CallAsync<string>(
                            "workspace.catalog.description",
                            null,
                            new
                            {
                                projectId = selectedProject.Id
                            },
                            ct
                        )
                    );
            }
        );
        DetailAction(
            "更新日志",
            async ct =>
            {
                if (selectedRelease is not null)
                    await TextDialog(
                        "更新日志 · " + selectedRelease.Name,
                        await Api.CallAsync<string>(
                            "workspace.catalog.changelog",
                            null,
                            new
                            {
                                releaseId = selectedRelease.Id
                            },
                            ct
                        )
                    );
            }
        );
        DetailAction(
            "另存为",
            async ct =>
            {
                if (selectedRelease is not { } release || TopLevel.GetTopLevel(this)is not Window owner)
                    return;
                var file = await owner.StorageProvider.SaveFilePickerAsync(
                    new()
                    {
                        Title = "保存资源文件",
                        SuggestedFileName = release.File.Name
                    }
                );
                var path = file?.TryGetLocalPath();
                if (path is null)
                    return;
                await Api.ExportAsync(
                    "workspace.browser.export",
                    profile()
                        .Id,
                    new
                    {
                        releaseId = release.Id
                    },
                    path,
                    ct
                );
                status.Text = "已保存并校验：" + path;
            }
        );
        DetailAction(
            "MC 百科",
            _ =>
            {
                if (selectedProject is { } project)
                    open(project.WikiUrl);
                else if (selectedManaged is { } managed)
                    open(
                        "https://www.mcmod.cn/s?key=" + Uri.EscapeDataString(managed.Name) + "&site=all&filter=0"
                    );
                return Task.CompletedTask;
            }
        );
        detail.Children.Add(more);
        previous.Click += async (_, _) =>
        {
            offset = Math.Max(0, offset - 20);
            await SearchAsync();
        };
        next.Click += async (_, _) =>
        {
            offset += 20;
            await SearchAsync();
        };
        paging.Children.Add(previous);
        paging.Children.Add(page);
        paging.Children.Add(next);
        Grid.SetRow(paging, 3);
        layout.Children.Add(paging);
        var feedback = new StackPanel
        {
            Spacing = 8
        };
        feedback.Children.Add(compatibility);
        feedback.Children.Add(status);
        feedback.Children.Add(cancel);
        cancel.Click += (_, _) => cancelOperation();
        Grid.SetRow(feedback, 4);
        layout.Children.Add(feedback);
        Content = layout;
        projects.SelectionChanged += async (_, _) =>
        {
            if (!busy && !suppressSelection)
                await SelectAsync();
        };
        releases.SelectionChanged += (_, _) =>
        {
            selectedRelease = releases.SelectedItem as ResourceRelease;
            install.IsEnabled = !busy && selectedRelease is not null;
        };
        install.Click += async (_, _) => await InstallSelectedAsync();
        support.Click += async (_, _) => await InstallShaderSupportAsync();
        Button Manage(string label, Func<CancellationToken, Task> action)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(12, 9)
            };
            button.Click += async (_, _) => await Execute(action);
            manageBar.Children.Add(button);
            return button;
        }

        Manage("刷新", RefreshManaged);
        Manage(
            "启用 / 禁用",
            async ct =>
            {
                if (selectedManaged is not null)
                {
                    await Api.CallAsync<bool>(
                        "workspace.browser.enable",
                        profile()
                            .Id,
                        new
                        {
                            projectId = selectedManaged.ProjectId,
                            enabled = !selectedManaged.Enabled
                        },
                        ct
                    );
                }

                await RefreshManaged(ct);
            }
        );
        Manage(
            "移除",
            async ct =>
            {
                if (selectedManaged is not null)
                {
                    await Api.CallAsync<bool>(
                        "workspace.browser.remove",
                        profile()
                            .Id,
                        new
                        {
                            projectId = selectedManaged.ProjectId
                        },
                        ct
                    );
                }

                await RefreshManaged(ct);
            }
        );
        Manage(
            "应用",
            async ct =>
            {
                if (selectedManaged is not null)
                {
                    await Api.CallAsync<bool>(
                        "workspace.browser.activate",
                        profile()
                            .Id,
                        new
                        {
                            projectId = selectedManaged.ProjectId
                        },
                        ct
                    );
                    status.Text = "已应用，下次启动游戏生效";
                }
            }
        );
        Manage(
            "撤销应用配置",
            async ct =>
            {
                await Api.CallAsync<bool>(
                    "workspace.browser.undo",
                    profile()
                        .Id,
                    ct: ct
                );
                status.Text = "已恢复应用前配置";
            }
        );
        Manage("卸载 Iris 环境", UninstallIrisEnvironmentAsync);
        Manage("更新", UpdateSelected);
        Manage("检查更新", CheckUpdates);
        Manage("导入文件", ImportAsync);
        Manage(
            "打开目录",
            _ =>
            {
                open(
                    profile()
                        .GameDirectory
                );
                return Task.CompletedTask;
            }
        );
        mutating.AddRange(
            new Control[]
            {
                search,
                query,
                sort,
                category,
                projects,
                releases,
                install,
                previous,
                next,
                support,
                importPackButton,
                optionalFiles,
                manageBar
            }
        );
        InitializeClassicResources();
    }

    private async Task TextDialog(string heading, string text)
    {
        if (TopLevel.GetTopLevel(this)is not Window owner)
            return;
        var dialog = new WorkspaceWindow
        {
            Title = heading,
            Width = 680,
            Height = 600,
            MinWidth = 420,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = string.IsNullOrWhiteSpace(text)
                    ? "暂无内容"
                    : text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18)
            }
        };
        await dialog.ShowDialog(owner);
    }

    private static Border Card(Control child) => new()
    {
        Background = Brush.Parse("#1C2330"),
        CornerRadius = new CornerRadius(18),
        Child = child
    };
    public void Report(string message) => status.Text = message;
    public void SetBusy(bool value)
    {
        busy = value;
        cancel.IsVisible = value;
        foreach (var control in mutating)
            control.IsEnabled = !value;
        install.IsEnabled = !value && selectedRelease is not null && !management;
        previous.IsEnabled = !value && offset > 0;
        next.IsEnabled = !value && offset + 20 < total;
    }

    public async Task ShowAsync(ResourceKind selectedKind, bool installed = false)
    {
        if (busy)
            return;
        kind = selectedKind;
        management = installed;
        await Execute(
            async ct =>
            {
                var settings = await Api.CallAsync<SettingsSnapshot>("workspace.settings.read", null, ct: ct);
                MainWindow.ApplyHiddenActions(this, settings.Appearance.HiddenRoutes);
            }
        );
        ResetClassicResourceLayout(installed);
        offset = 0;
        query.Text = "";
        heading.Text = installed
            ? "已安装"
            : kind switch
            {
                ResourceKind.Mod => "模组",
                ResourceKind.Shader => "光影",
                ResourceKind.ResourcePack => "资源包",
                ResourceKind.DataPack => "数据包",
                _ => "整合包"
            };
        optionalFiles.IsVisible = importPackButton.IsVisible = !installed && kind == ResourceKind.Modpack;
        install.Content = kind == ResourceKind.Modpack
            ? "安装整合包"
            : kind == ResourceKind.DataPack
                ? "选择世界并安装"
                : "安装（含前置）";
        searchBar.IsVisible = !installed;
        manageBar.IsVisible = installed;
        paging.IsVisible = !installed;
        category.IsVisible = !installed;
        compatibility.IsVisible = false;
        await RefreshBackendHints();
        support.IsVisible = !installed && kind == ResourceKind.Shader;
        releases.IsVisible = !installed;
        install.IsVisible = !installed;
        if (webButton is not null)
            webButton.IsVisible = !installed;
        if (installed)
        {
            try
            {
                await RefreshManaged();
                status.Text = "";
            }
            catch (Exception ex)
            {
                projects.ItemsSource = null;
                status.Text = ex.Message;
            }
        }
        else
        {
            await Execute(
                async ct =>
                {
                    category.ItemsSource = new[]
                    {
                        "全部分类"
                    }.Concat(
                        await Api.CallAsync<string[]>(
                            "workspace.catalog.categories",
                            null,
                            new
                            {
                                kind = (int)kind
                            },
                            ct
                        )
                    )
                        .ToArray();
                    category.SelectedIndex = 0;
                }
            );
            await SearchAsync();
        }
    }

    private async Task Execute(Func<CancellationToken, Task> action) => await run(action);
    private async Task SearchAsync()
    {
        if (RouteClassicSource())
        {
            return;
        }

        await Execute(
            async ct =>
            {
                status.Text = "搜索中…";
                selectedRelease = null;
                install.IsEnabled = false;
                var results = await Api.CallAsync<ResourceSearch>(
                    "workspace.catalog.search",
                    null,
                    new
                    {
                        kind = (int)kind,
                        query = query.Text ?? "",
                        gameVersion = ClassicEnabled
                            ? BrowseVersion
                            : TargetVersion,
                        loader = ClassicEnabled
                            ? BrowseLoader
                            : TargetLoader,
                        offset,
                        sort = sort.SelectedIndex switch
                        {
                            1 => "downloads",
                            2 => "updated",
                            _ => "relevance"
                        },
                        category = category.SelectedIndex > 0
                            ? category.SelectedItem?.ToString()
                            : null
                    },
                    ct
                );
                projects.ItemsSource = results.Projects.Select(CreateClassicProjectRow)
                    .ToArray();
                total = results.Total;
                previous.IsEnabled = offset > 0;
                next.IsEnabled = offset + 20 < results.Total;
                page.Text = $"{offset / 20 + 1} / {Math.Max(1, (results.Total + 19) / 20)}";
                title.Text = "";
                description.Text = "";
                releases.ItemsSource = null;
                status.Text = kind == ResourceKind.Shader
                    ? "Mac 光影为实验兼容 · 应用前检查必需功能"
                    : $"Modrinth · {(ClassicEnabled
                            ? (BrowseVersion.Length == 0
                                ? "全部版本"
                                : BrowseVersion)
                            : TargetVersion)} · {(kind == ResourceKind.Modpack
                            ? "整合包"
                            : ClassicEnabled
                                ? (BrowseLoader.Length == 0
                                    ? "全部加载器"
                                    : BrowseLoader)
                                : TargetLoader)} · {results.Total:N0} 个项目";
                var translated = results.TranslatedQuery;
                if (translated != query.Text)
                    status.Text += " · 中文匹配：" + translated;
            }
        );
    }

    private async Task SelectAsync()
    {
        if (management)
        {
            selectedManaged = (projects.SelectedItem as ListBoxItem)?.Tag as ManagedResource;
            title.Text = selectedManaged?.Name ?? "";
            description.Text = selectedManaged is null
                ? ""
                : $"{selectedManaged.FileName}\n{selectedManaged.Size / 1048576.0:F1} MiB";
            releases.ItemsSource = null;
            selectedRelease = null;
            install.IsEnabled = false;
            return;
        }

        var project = (projects.SelectedItem as ListBoxItem)?.Tag as ResourceProject;
        if (project is null)
            return;
        selectedProject = project;
        var revision = ++selectionRevision;
        await Execute(
            async ct =>
            {
                title.Text = project.Title;
                description.Text = project.Description;
                var versions = (IReadOnlyList<ResourceRelease>)await Api.CallAsync<ResourceRelease[]>(
                    "workspace.catalog.versions",
                    null,
                    new
                    {
                        projectId = project.Id,
                        kind = (int)kind,
                        gameVersion = ClassicEnabled
                            ? BrowseVersion
                            : TargetVersion,
                        loader = ClassicEnabled
                            ? BrowseLoader
                            : TargetLoader
                    },
                    ct
                );
                if (revision != selectionRevision)
                    return;
                releases.ItemsSource = versions;
                releases.SelectedItem = versions.FirstOrDefault(
                    x => x.Channel == "release"
                ) ?? versions.FirstOrDefault();
                selectedRelease = releases.SelectedItem as ResourceRelease;
                ShowClassicDetail(project, versions);
                status.Text = versions.Count == 0
                    ? "没有适用版本"
                    : $"{versions.Count} 个版本 · 安装时包含必需前置";
            }
        );
    }

    private async Task InstallSelectedAsync()
    {
        if (selectedRelease is not { } release)
            return;
        // “全部版本”用于浏览，安装仍锁定当前实例，禁止把其他版本文件写入实例。
        if (kind != ResourceKind.Modpack && (!release.GameVersions.Contains(TargetVersion) || (kind == ResourceKind.Mod && !release.Loaders.Contains(TargetLoader))))
        {
            status.Text = $"此文件不匹配当前实例 {TargetVersion} / {TargetLoader}。请选择兼容版本，或使用另存为。";
            return;
        }

        await Execute(
            async ct =>
            {
                if (kind == ResourceKind.Modpack)
                {
                    var result = await installPack(release, optionalFiles.IsChecked == true, ct);
                    status.Text = "已安装 · " + result.Name;
                    return;
                }

                if (kind == ResourceKind.DataPack)
                {
                    if (TopLevel.GetTopLevel(this)is not Window owner)
                        return;
                    var worlds = await Api.CallAsync<Pcl.Core.Contracts.Workspaces.WorldDirectory[]>(
                        "workspace.datapacks.worlds",
                        profile()
                            .Id,
                        ct: ct
                    );
                    if (worlds.Length == 0)
                        throw new InvalidOperationException("当前实例没有世界，请先进入游戏创建世界。");
                    var choices = new ComboBox
                    {
                        ItemsSource = worlds,
                        SelectedIndex = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    var confirm = new Button
                    {
                        Content = "安装到此世界",
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    var dialog = new WorkspaceWindow
                    {
                        Title = "选择世界",
                        Width = 400,
                        SizeToContent = SizeToContent.Height,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(24),
                            Spacing = 16,
                            Children =
                            {
                                choices,
                                confirm
                            }
                        }
                    };
                    confirm.Click += (_, _) => dialog.Close(choices.SelectedItem as Pcl.Core.Contracts.Workspaces.WorldDirectory);
                    var world = await dialog.ShowDialog<Pcl.Core.Contracts.Workspaces.WorldDirectory?>(owner);
                    if (world is null)
                        return;
                    await Api.CallAsync<bool>(
                        "workspace.browser.datapack",
                        profile()
                            .Id,
                        new
                        {
                            world = world.Path,
                            releaseId = release.Id
                        },
                        ct
                    );
                    status.Text = "已安装到 " + world.Name + " · 重新进入世界后生效";
                    return;
                }

                await ensureFabric(ct);
                if (kind == ResourceKind.Shader)
                    await EnsureIrisAsync(ct);
                await Api.CallAsync<bool>(
                    "workspace.browser.install",
                    profile()
                        .Id,
                    new
                    {
                        releaseId = release.Id,
                        kind = (int)kind
                    },
                    ct,
                    new Progress<Pcl.Core.Contracts.Installation.VersionInstallProgress>(
                        p => status.Text = p.Stage
                    )
                );
                status.Text = "已安装 · " + release.Name;
            }
        );
    }

    public async Task InstallShaderSupportAsync() => await Execute(
        async ct =>
        {
            status.Text = "安装 Fabric / Iris…";
            await ensureFabric(ct);
            await EnsureIrisAsync(ct);
            status.Text = "光影支持已安装 · 在已安装页应用光影包";
        }
    );
    // Host 重新读取版本元数据并负责依赖求解、下载、安装和校验，UI 只提交版本标识。
    private async Task EnsureIrisAsync(CancellationToken ct) => await Api.CallAsync<bool>(
        "workspace.browser.iris",
        profile()
            .Id,
        ct: ct
    );
    // 后端说明是可选展示：只有 Host 返回实际组件时才显示；未安装实例不推断后端。
    // 说明对话框不持有任何业务计划；取消只关闭窗口，不发送卸载命令。
    internal string IrisTestStatus => status.Text ?? "";

    private async Task UninstallIrisEnvironmentAsync(CancellationToken ct)
    {
        var instanceId = profile()
            .Id;
        var plan = await Api.CallAsync<ShaderEnvironmentPreview>("workspace.browser.irisPreview", instanceId, ct: ct);
        if (TopLevel.GetTopLevel(this)is not Window owner)
        {
            return;
        }

        var accepted = await new IrisEnvironmentWindow(plan).ShowDialog<bool>(owner);
        if (!accepted)
        {
            status.Text = "已取消，未卸载环境。";
            return;
        }

        var result = await Api.CallAsync<ShaderEnvironmentResult>(
            "workspace.browser.irisUninstall",
            instanceId,
            new
            {
                fingerprint = plan.Fingerprint
            },
            ct
        );
        await RefreshManaged(ct);
        status.Text = "已卸载 Iris 环境，移除 " + result.Removed.Length + " 个组件；共享组件和光影包保留。备份：" + result.BackupDirectory;
    }

    private async Task RefreshBackendHints(CancellationToken ct = default)
    {
        compatibility.Text = "";
        compatibility.IsVisible = false;
        try
        {
            var backends = await Api.CallAsync<string[]>(
                "workspace.browser.backends",
                profile()
                    .Id,
                ct: ct
            );
            compatibility.Text = string.Join("\n", backends);
            compatibility.IsVisible = backends.Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 该说明不阻塞无已安装实例时的公开目录搜索；资源操作仍走原错误处理路径。
        }
    }

    private async Task RefreshManaged(CancellationToken ct = default)
    {
        selectedManaged = null;
        selectedRelease = null;
        selectedProject = null;
        projects.ItemsSource = (await Api.CallAsync<ManagedResource[]>(
            "workspace.browser.display",
            profile()
                .Id,
            ct: ct
        )).Select(
            x => new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = x.Display,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(8)
                },
                Tag = x
            }
        )
            .ToArray();
        await RefreshBackendHints(ct);
        title.Text = "";
        description.Text = "";
        releases.ItemsSource = null;
        install.IsEnabled = false;
        if (projects.ItemCount > 0)
        {
            projects.SelectedIndex = 0;
            selectedManaged = (projects.SelectedItem as ListBoxItem)?.Tag as ManagedResource;
            title.Text = selectedManaged?.Name ?? "";
            description.Text = selectedManaged is null
                ? ""
                : selectedManaged.FileName;
        }
    }

    private async Task UpdateSelected(CancellationToken ct)
    {
        if (selectedManaged is null)
        {
            return;
        }

        var message = await Api.CallAsync<string>(
            "workspace.browser.update",
            profile()
                .Id,
            new
            {
                projectId = selectedManaged.ProjectId
            },
            ct
        );
        await RefreshManaged(ct);
        status.Text = message;
    }

    private async Task CheckUpdates(CancellationToken ct)
    {
        var changes = await Api.CallAsync<string[]>(
            "workspace.browser.updates",
            profile()
                .Id,
            ct: ct
        );
        status.Text = changes.Length == 0
            ? "暂无兼容更新"
            : "可更新：" + string.Join("；", changes);
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        var owner = TopLevel.GetTopLevel(this);
        if (owner is null)
            return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "导入模组 / 光影 / 资源包",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Minecraft 资源")
                    {
                        Patterns = new[]
                        {
                            "*.jar",
                            "*.litemod",
                            "*.zip"
                        }
                    }
                }
            }
        );
        if (files.Count == 0)
            return;
        await ensureFabric(ct);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath() ?? throw new IOException("文件没有本地路径。");
            await Api.CallAsync<ManagedResource>(
                "workspace.browser.import",
                profile()
                    .Id,
                new
                {
                    path
                },
                ct
            );
        }

        await RefreshManaged();
        status.Text = $"已导入 {files.Count} 个文件";
    }

    public async Task SmokeAsync(ResourceKind resourceKind, string search, CancellationToken ct = default)
    {
        kind = resourceKind;
        heading.Text = kind switch
        {
            ResourceKind.Shader => "光影",
            ResourceKind.Modpack => "整合包",
            _ => "模组"
        };
        optionalFiles.IsVisible = importPackButton.IsVisible = kind == ResourceKind.Modpack;
        install.Content = kind == ResourceKind.Modpack
            ? "安装整合包"
            : "安装（含前置）";
        support.IsVisible = kind == ResourceKind.Shader;
        await RefreshBackendHints(ct);
        query.Text = search;
        await SearchAsync();
        if (projects.ItemCount > 0)
        {
            suppressSelection = true;
            projects.SelectedIndex = 0;
            suppressSelection = false;
            await SelectAsync();
        }
    }

    public void Dispose()
    {
        DisposeClassicResources();
    }
}
