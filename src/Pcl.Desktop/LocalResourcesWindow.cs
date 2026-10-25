using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
// 本地资源管理：扫描模组、光影和资源包，在文件变更前后校验依赖并保留事务恢复信息。
public sealed class LocalResourcesWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private LocalResourceInfo[] cached = [];
    private string nameStyle = "name";
    private readonly GameProfile profile;
    private readonly ListBox files = new()
    {
        Height = 230,
        SelectionMode = SelectionMode.Multiple
    };
    private readonly ListBox updates = new()
    {
        Height = 120,
        SelectionMode = SelectionMode.Multiple
    };
    private readonly ComboBox worlds = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ListBox packs = new()
    {
        Height = 110
    };
    private readonly TextBox search = new()
    {
        PlaceholderText = "搜索本地资源"
    };
    private readonly ComboBox filter = new()
    {
        ItemsSource = new[]
        {
            "全部资源",
            "模组",
            "光影",
            "资源包",
            "已启用",
            "已停用",
            "异常文件"
        },
        SelectedIndex = 0
    };
    private readonly TextBlock status = new()
    {
        Name = "LocalDependencyStatus",
        TextWrapping = TextWrapping.Wrap
    };
    private CancellationTokenSource? cancellation;
    private readonly List<Button> buttons = new();
    public bool Busy => cancellation is not null;

    public LocalResourcesWindow(IWorkspaceOperations operations, GameProfile profile)
    {
        this.profile = profile;
        api = new(operations);
        Title = "本地资源 · " + profile.Name;
        Width = 760;
        Height = 790;
        MinWidth = 620;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "本地资源",
                FontSize = 24
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = $"Minecraft {profile.GameVersion} · {profile.Loader}",
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(search);
        panel.Children.Add(filter);
        panel.Children.Add(files);
        filter.SelectionChanged += (_, _) => Refresh();
        search.TextChanged += (_, _) => Refresh();
        files.SelectionChanged += (_, _) =>
        {
            if (files.SelectedItem is LocalResourceInfo item)
                status.Text = item.Path + "\n" + (item.Error ?? string.Join(", ", item.ModIds));
        };
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        row.Children.Add(
            Button("刷新", ReloadAsync)
        );
        row.Children.Add(
            Button(
                "启用",
                async () =>
                {
                    await Call<bool>(
                        "resources.enable",
                        new
                        {
                            paths = Selected(),
                            enabled = true
                        }
                    );
                    await ReloadAsync();
                }
            )
        );
        row.Children.Add(
            Button(
                "停用",
                async () =>
                {
                    await Call<bool>(
                        "resources.enable",
                        new
                        {
                            paths = Selected(),
                            enabled = false
                        }
                    );
                    await ReloadAsync();
                }
            )
        );
        row.Children.Add(
            Button(
                "移至备份",
                async () =>
                {
                    status.Text = "已备份至 " + await Call<string>(
                        "resources.remove",
                        new
                        {
                            paths = Selected()
                        }
                    );
                    await ReloadAsync();
                }
            )
        );
        row.Children.Add(
            Button(
                "检查更新",
                async () =>
                {
                    updates.ItemsSource = await Call<LocalResourceUpdate[]>("resources.updates");
                    status.Text = "更新检查完成";
                }
            )
        );
        panel.Children.Add(row);
        row.Children.Add(
            Button(
                "检查依赖关系",
                async () =>
                {
                    var warnings = await Call<string[]>("resources.validate");
                    status.Text = profile.Loader is "fabric" or "quilt" or "forge" or "neoforge"
                        ? "已检查当前启用模组的可解析依赖、版本范围与冲突。游戏仍会校验其他运行条件。\n" + string.Join('\n', warnings)
                        : "当前加载器尚无完整依赖分析器；已扫描本地元数据。";
                }
            )
        );
        row.Children.Add(
            Button(
                "操作历史",
                async () =>
                {
                    var history = new ListBox
                    {
                        ItemsSource = await Call<string[]>("resources.history"),
                        Margin = new Thickness(18)
                    };
                    var dialog = new WorkspaceWindow
                    {
                        Title = "资源操作历史",
                        Width = 700,
                        Height = 480,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = history
                    };
                    await dialog.ShowDialog(this);
                }
            )
        );
        row.Children.Add(
            Button(
                "撤销应用",
                async () =>
                {
                    await Call<bool>("resources.undo");
                    status.Text = "已恢复应用前的配置";
                }
            )
        );
        panel.Children.Add(updates);
        var updateRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        updateRow.Children.Add(
            Button(
                "安装所选更新",
                async () =>
                {
                    if (updates.SelectedItem is not LocalResourceUpdate update)
                        throw new InvalidOperationException("请选择更新。");
                    await Call<bool>(
                        "resources.update",
                        new
                        {
                            selection = update.SelectionId
                        }
                    );
                    await ReloadAsync();
                    updates.ItemsSource = null;
                    status.Text = "更新完成";
                }
            )
        );
        updateRow.Children.Add(
            Button(
                "批量更新所选",
                async () =>
                {
                    var selected = updates.SelectedItems?.Cast<LocalResourceUpdate>()
                        .ToArray() ?? [];
                    var prepared = await Call<WorkspaceBatch>(
                        "resources.batchPrepare",
                        new
                        {
                            selections = selected.Select(
                                x => x.SelectionId
                            )
                                .ToArray()
                        }
                    );
                    var committed = false;
                    try
                    {
                        var confirm = new Button
                        {
                            Content = "提交整批更新"
                        };
                        var preview = new WorkspaceWindow
                        {
                            Title = "批量更新预览",
                            Width = 650,
                            Height = 480,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                            Content = new StackPanel
                            {
                                Margin = new Thickness(24),
                                Spacing = 12,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "下载及依赖检查已完成。提交后保留旧文件备份。",
                                        TextWrapping = TextWrapping.Wrap
                                    },
                                    new ListBox
                                    {
                                        Height = 260,
                                        ItemsSource = prepared.Changes
                                    },
                                    new TextBlock
                                    {
                                        Text = string.Join('\n', prepared.Warnings),
                                        TextWrapping = TextWrapping.Wrap
                                    },
                                    confirm
                                }
                            }
                        };
                        confirm.Click += (_, _) => preview.Close(true);
                        if (await preview.ShowDialog<bool>(this))
                        {
                            var backup = await Call<string>(
                                "resources.batchApply",
                                new
                                {
                                    handle = prepared.Handle
                                }
                            );
                            committed = true;
                            await ReloadAsync();
                            updates.ItemsSource = await Call<LocalResourceUpdate[]>("resources.updates");
                            status.Text = "批量更新完成，旧文件备份：" + backup;
                        }
                        else
                            status.Text = "已取消批量更新，当前资源保持原样";
                    }
                    finally
                    {
                        if (!committed)
                        {
                            try
                            {
                                await api.CallAsync<string>(
                                    "workspace.resources.batchDiscard",
                                    profile.Id,
                                    new
                                    {
                                        handle = prepared.Handle
                                    }
                                );
                            }
                            catch (Exception ex)
                            {
                                status.Text = "预览清理未完成：" + ex.Message;
                            }
                        }
                    }
                }
            )
        );
        updateRow.Children.Add(
            Button(
                "忽略此版本",
                async () =>
                {
                    if (updates.SelectedItem is LocalResourceUpdate update)
                        await Call<bool>(
                            "resources.ignore",
                            new
                            {
                                selection = update.SelectionId
                            }
                        );
                    updates.ItemsSource = null;
                    status.Text = "已忽略所选版本";
                }
            )
        );
        panel.Children.Add(updateRow);
        panel.Children.Add(
            new Separator()
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "世界数据包",
                FontSize = 20
            }
        );
        panel.Children.Add(worlds);
        panel.Children.Add(packs);
        worlds.SelectionChanged += async (_, _) =>
        {
            if (!Busy)
            {
                await Run(RefreshPacks);
            }
        };
        worlds.SelectedIndex = 0;
        var dataRow = new WrapPanel();
        dataRow.Children.Add(
            Button(
                "安装数据包",
                async () =>
                {
                    var world = World();
                    var selected = await StorageProvider.OpenFilePickerAsync(
                        new()
                        {
                            Title = "安装世界数据包",
                            FileTypeFilter = [
                                new("数据包")
                                {
                                    Patterns = [
                                        "*.zip"
                                    ]
                                }
                            ]
                        }
                    );
                    var source = selected.FirstOrDefault()?.TryGetLocalPath();
                    if (source is null)
                        return;
                    await Call<string>(
                        "datapacks.install",
                        new
                        {
                            world = world.Path,
                            source
                        }
                    );
                    await RefreshPacks();
                    status.Text = "已安装；重新进入世界后生效。数据包版本兼容性由游戏校验。";
                }
            )
        );
        dataRow.Children.Add(
            Button(
                "启用 / 停用",
                async () =>
                {
                    if (packs.SelectedItem is WorldDataPack item)
                    {
                        await Call<bool>(
                            "datapacks.enable",
                            new
                            {
                                world = World()
                                    .Path,
                                path = item.Path,
                                enabled = !item.Enabled
                            }
                        );
                    }

                    await RefreshPacks();
                }
            )
        );
        dataRow.Children.Add(
            Button(
                "移至备份",
                async () =>
                {
                    if (packs.SelectedItem is WorldDataPack item)
                    {
                        status.Text = "已备份至 " + await Call<string>(
                            "datapacks.remove",
                            new
                            {
                                world = World()
                                    .Path,
                                path = item.Path
                            }
                        );
                    }

                    await RefreshPacks();
                }
            )
        );
        panel.Children.Add(dataRow);
        var cancel = new Button
        {
            Content = "取消任务"
        };
        cancel.Click += (_, _) => cancellation?.Cancel();
        panel.Children.Add(cancel);
        if (operations is IPageTaskSource)
        {
            panel.Children.Add(
                Button(
                    "后台任务",
                    () => new PageTasksWindow(operations.Tasks).ShowDialog(this)
                )
            );
        }

        panel.Children.Add(status);
        Content = new ScrollViewer
        {
            Content = panel
        };
        // 保持列表项的真实对象供选择/更新使用，模板只改变名称与文件名的显示顺序。
        files.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LocalResourceInfo>(
            (item, _) => new TextBlock
            {
                Text = item is null
                    ? ""
                    : (nameStyle == "file"
                        ? $"{Path.GetFileName(item.Path)}\n{item.Name} · {item.Version}"
                        : $"{item.Name} · {item.Version}\n{Path.GetFileName(item.Path)}") + (item.Error is null
                        ? ""
                        : " · 文件异常"),
                TextWrapping = TextWrapping.Wrap
            }
        );
        Opened += async (_, _) => await Run(
            async () =>
            {
                await ReloadAsync();
                worlds.ItemsSource = await Call<WorldDirectory[]>("datapacks.worlds");
                worlds.SelectedIndex = 0;
                await RefreshPacks();
            }
        );
        Closing += (_, e) =>
        {
            if (Busy)
            {
                cancellation?.Cancel();
                e.Cancel = true;
                status.Text = "正在等待 Host 安全结束任务";
            }
        };
    }

    private string[] Selected() => files.SelectedItems?.Cast<LocalResourceInfo>()
        .Select(
            x => x.Path
        )
        .ToArray()is { Length: > 0 } values
        ? values
        : throw new InvalidOperationException("请选择资源。");
    private WorldDirectory World() => worlds.SelectedItem as WorldDirectory ?? throw new InvalidOperationException("此实例还没有可选世界，请先进入游戏创建世界。");
    private void Refresh() => files.ItemsSource = cached.Where(
        x => (x.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x.Path)
            .Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)) && (filter.SelectedIndex switch
        {
            1 => x.Kind == ResourceKind.Mod,
            2 => x.Kind == ResourceKind.Shader,
            3 => x.Kind == ResourceKind.ResourcePack,
            4 => x.Enabled,
            5 => !x.Enabled,
            6 => x.Error is not null,
            _ => true
        })
    )
        .ToArray();
    // 仅过滤缓存的展示数据；扫描、依赖校验及写入均在 Host 中完成。
    private Task<T> Call<T>(string method, object? args = null) => api.CallAsync<T>("workspace." + method, profile.Id, args, cancellation?.Token ?? default);
    private async Task ReloadAsync()
    {
        var snapshot = await Call<ResourceScan>("resources.scan");
        cached = snapshot.Items;
        nameStyle = snapshot.ModNameStyle;
        Refresh();
    }

    private async Task RefreshPacks()
    {
        packs.ItemsSource = worlds.SelectedItem is WorldDirectory world
            ? await Call<WorldDataPack[]>(
                "datapacks.list",
                new
                {
                    world = world.Path
                }
            )
            : [];
    }

    private async Task Run(Func<Task> action)
    {
        if (Busy)
        {
            return;
        }

        cancellation = new();
        foreach (var button in buttons)
        {
            button.IsEnabled = false;
        }

        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            status.Text = "已请求取消";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            foreach (var button in buttons)
            {
                button.IsEnabled = true;
            }
        }
    }

    private Button Button(string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = title,
            Margin = new Thickness(0, 0, 6, 0)
        };
        buttons.Add(button);
        button.Click += async (_, _) => await Run(action);
        return button;
    }
}
