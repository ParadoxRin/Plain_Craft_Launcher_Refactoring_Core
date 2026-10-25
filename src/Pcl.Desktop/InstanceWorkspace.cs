using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
/// <summary>Standalone instance management surface, shared by the main navigation and UI verification.</summary>
public sealed class InstanceWorkspace : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly Func<string, IComponentPageService> components;
    private InstanceSnapshot snapshot = new(
        [],
        new(),
        ""
    );
    private readonly Dictionary<string, byte[]> icons = new();
    private readonly InstanceViewCache manager = new();
    private readonly ListBox list = new()
    {
        Name = "InstanceList"
    };
    /// <summary>用独立值表示全部与未分组，用户名称与界面提示重名时仍能精确筛选。</summary>
    private sealed record GroupChoice(string Label, string? Value)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox groupFilter = new()
    {
        Name = "InstanceGroupFilter",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private bool refreshingGroups;
    private readonly TextBox search = new()
    {
        PlaceholderText = "搜索实例"
    };
    private readonly ComboBox loaderFilter = new()
    {
        ItemsSource = new[]
        {
            "全部加载器",
            "vanilla",
            "fabric",
            "quilt",
            "forge",
            "neoforge",
            "liteloader",
            "optifine"
        },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ComboBox visibilityFilter = new()
    {
        ItemsSource = new[]
        {
            "全部实例",
            "仅收藏",
            "仅隐藏",
            "未隐藏"
        },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock status = new()
    {
        Name = "InstanceStatus",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly StackPanel details = new()
    {
        Spacing = 12,
        Margin = new Thickness(0, 0, 16, 0)
    };
    private readonly Button cancel = new()
    {
        Content = "取消任务",
        IsVisible = false
    };
    private readonly Action<string?>? changed;
    private CancellationTokenSource? operation;
    public bool Busy => operation is not null;

    private Bitmap? instanceIcon;
    public InstanceWorkspace(
        IWorkspaceOperations operations,
        Func<string, IComponentPageService> components,
        string? selectedId = null,
        Action<string?>? changed = null
    )
    {
        api = new(operations);
        this.components = components;
        this.changed = changed;
        Title = "实例管理";
        Width = 1000;
        Height = 740;
        MinWidth = 800;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(22),
            LastChildFill = true
        };
        var header = new WrapPanel
        {
            ItemSpacing = 10,
            LineSpacing = 10,
            Margin = new Thickness(0, 0, 0, 16)
        };
        header.Children.Add(
            new TextBlock
            {
                Text = "实例管理",
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            }
        );
        if (operations is IPageTaskSource)
        {
            header.Children.Add(
                Button(
                    "后台任务",
                    () => new PageTasksWindow(operations.Tasks).ShowDialog(this)
                )
            );
        }

        header.Children.Add(
            Button("导入目录", ImportDirectory)
        );
        header.Children.Add(
            Button("恢复备份", RestoreBackup)
        );
        header.Children.Add(
            Button("校验备份", VerifyBackup)
        );
        header.Children.Add(
            Button("回收站", ShowTrash)
        );
        header.Children.Add(
            Button(
                "全局启动设置",
                () =>
                {
                    ShowLaunchSettings(null);
                    return Task.CompletedTask;
                }
            )
        );
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        footer.Children.Add(status);
        footer.Children.Add(cancel);
        cancel.Click += (_, _) => operation?.Cancel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var left = new DockPanel
        {
            Width = 265,
            Margin = new Thickness(0, 0, 24, 0)
        };
        DockPanel.SetDock(left, Dock.Left);
        var filters = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10)
        };
        filters.Children.Add(search);
        filters.Children.Add(loaderFilter);
        filters.Children.Add(visibilityFilter);
        filters.Children.Add(groupFilter);
        DockPanel.SetDock(filters, Dock.Top);
        left.Children.Add(filters);
        left.Children.Add(list);
        root.Children.Add(left);
        root.Children.Add(
            new ScrollViewer
            {
                Content = details
            }
        );
        Content = root;
        search.TextChanged += (_, _) => Refresh();
        loaderFilter.SelectionChanged += (_, _) => Refresh();
        visibilityFilter.SelectionChanged += (_, _) => Refresh();
        groupFilter.SelectionChanged += (_, _) =>
        {
            if (!refreshingGroups)
            {
                Refresh();
            }
        };
        list.SelectionChanged += (_, _) => ShowInstance();
        Closed += (_, _) => instanceIcon?.Dispose();
        Closing += (_, e) =>
        {
            if (operation is not null)
            {
                operation.Cancel();
                e.Cancel = true;
                status.Text = "正在等待 Host 安全结束任务。";
            }
        };
        Opened += async (_, _) => await Run(
            ct => ReloadAsync(selectedId, ct)
        );
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text
        };
        button.Click += async (_, _) =>
        {
            if (operation is not null)
                return;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        };
        return button;
    }

    // 从 Host 取得展示快照和受控图标。筛选只操作内存，不重新执行磁盘业务。
    private async Task ReloadAsync(string? id, CancellationToken ct, string? group = null)
    {
        snapshot = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null, ct: ct);
        manager.Snapshot = snapshot;
        icons.Clear();
        foreach (var item in snapshot.Items)
        {
            var bytes = await api.ReadIconAsync(item.Profile.Id, ct);
            if (bytes is not null)
            {
                icons[item.Profile.Id] = bytes;
            }
        }

        Refresh(id, group);
    }

    private void Refresh(string? id = null, string? selectGroup = null)
    {
        id ??= (list.SelectedItem as GameProfile)?.Id;
        // 更新分组选项时屏蔽递归事件；保存分组后保留当前实例可见，删除最后成员后回到全部。
        var desiredGroup = selectGroup ?? (groupFilter.SelectedItem as GroupChoice)?.Value;
        var groups = new[]
        {
            new GroupChoice("全部分组", null),
            new GroupChoice("未分组", "")
        }.Concat(
            manager.Groups()
                .Select(
                    value => new GroupChoice("分组 · " + value, value)
                )
        )
            .ToArray();
        refreshingGroups = true;
        try
        {
            groupFilter.ItemsSource = groups;
            groupFilter.SelectedItem = groups.FirstOrDefault(
                choice => string.Equals(choice.Value, desiredGroup, StringComparison.OrdinalIgnoreCase)
            ) ?? groups[0];
        }
        finally
        {
            refreshingGroups = false;
        }

        var profiles = manager.Find(
            search.Text,
            loaderFilter.SelectedIndex <= 0
                ? null
                : loaderFilter.SelectedItem as string,
            visibilityFilter.SelectedIndex == 1,
            visibilityFilter.SelectedIndex == 2,
            visibilityFilter.SelectedIndex != 3,
            (groupFilter.SelectedItem as GroupChoice)?.Value
        )
            .ToArray();
        list.ItemsSource = profiles;
        list.SelectedItem = profiles.FirstOrDefault(
            x => x.Id == id
        ) ?? profiles.FirstOrDefault();
        if (profiles.Length == 0)
        {
            details.Children.Clear();
            details.Children.Add(
                new TextBlock
                {
                    Text = "没有匹配的实例。可导入游戏目录，或返回首页下载。",
                    TextWrapping = TextWrapping.Wrap
                }
            );
        }
    }

    private void ShowInstance()
    {
        if (list.SelectedItem is not GameProfile profile)
            return;
        details.Children.Clear();
        var preferences = manager.Settings.Preferences(profile.GameDirectory);
        instanceIcon?.Dispose();
        instanceIcon = null;
        try
        {
            if (icons.TryGetValue(profile.Id, out var image))
            {
                instanceIcon = new Bitmap(
                    new MemoryStream(image)
                );
                details.Children.Add(
                    new Image
                    {
                        Source = instanceIcon,
                        Width = 72,
                        Height = 72,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    }
                );
            }
        }
        catch (Exception ex)
        {
            status.Text = "图标无法读取，可重新导入：" + ex.Message;
        }

        details.Children.Add(
            new TextBlock
            {
                Text = profile.Name,
                FontSize = 23,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        );
        details.Children.Add(
            new TextBlock
            {
                Text = profile.GameVersion + " · " + profile.Loader + "\n" + profile.GameDirectory,
                TextWrapping = TextWrapping.Wrap,
                Opacity = .7
            }
        );
        var origin = snapshot.Items.FirstOrDefault(
            x => x.Profile.Id == profile.Id
        )?.OriginDirectory;
        if (!string.IsNullOrWhiteSpace(origin))
        {
            details.Children.Add(
                new TextBlock
                {
                    Text = "副本来源：" + origin + "\n当前副本：" + profile.GameDirectory,
                    TextWrapping = TextWrapping.Wrap
                }
            );
        }

        var name = new TextBox
        {
            Text = profile.Name,
            PlaceholderText = "名称"
        };
        var description = new TextBox
        {
            Text = preferences.Description,
            PlaceholderText = "描述",
            AcceptsReturn = true,
            Height = 80
        };
        var group = new TextBox
        {
            Name = "InstanceGroupName",
            Text = preferences.Group,
            PlaceholderText = "分组（留空为未分组）",
            MaxLength = 60
        };
        var favorite = new CheckBox
        {
            Content = "收藏",
            IsChecked = preferences.Favorite
        };
        var hidden = new CheckBox
        {
            Content = "在首页隐藏",
            IsChecked = preferences.Hidden
        };
        details.Children.Add(name);
        details.Children.Add(description);
        details.Children.Add(group);
        details.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 22,
                Children =
                {
                    favorite,
                    hidden
                }
            }
        );
        details.Children.Add(
            Button(
                "保存",
                () => Run(
                    async ct =>
                    {
                        await api.CallAsync<bool>(
                            "workspace.instances.savePreferences",
                            profile.Id,
                            new
                            {
                                value = preferences with
                                {
                                    Name = name.Text?.Trim() ?? "",
                                    Description = description.Text ?? "",
                                    Favorite = favorite.IsChecked == true,
                                    Hidden = hidden.IsChecked == true,
                                    Group = group.Text ?? ""
                                }
                            },
                            ct
                        );
                        await ReloadAsync(
                            profile.Id,
                            ct,
                            group.Text?.Trim()
                        );
                        changed?.Invoke(profile.Id);
                        status.Text = "实例信息已保存";
                    }
                )
            )
        );
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };
        actions.Children.Add(
            Button(
                "导出离线启动脚本",
                async () =>
                {
                    var window = new OfflineScriptWindow(api, profile.Id);
                    await window.ShowDialog(this);
                }
            )
        );
        actions.Children.Add(
            Button(
                "启动设置",
                () =>
                {
                    ShowLaunchSettings(profile);
                    return Task.CompletedTask;
                }
            )
        );
        actions.Children.Add(
            Button(
                "更换图标",
                async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new FilePickerOpenOptions
                        {
                            Title = "选择实例图标",
                            AllowMultiple = false,
                            FileTypeFilter = [
                                new FilePickerFileType("PNG / JPEG")
                                {
                                    Patterns = [
                                        "*.png",
                                        "*.jpg",
                                        "*.jpeg"
                                    ]
                                }
                            ]
                        }
                    );
                    if (files.FirstOrDefault()?.TryGetLocalPath()is not string file)
                        return;
                    await Run(
                        async ct =>
                        {
                            if (new FileInfo(file).Length > 4 * 1024 * 1024)
                            {
                                throw new InvalidDataException("图标不能超过 4 MiB。");
                            }

                            using var bitmap = new Bitmap(file);
                            var temporary = Path.Combine(
                                Path.GetTempPath(),
                                "pcl-icon-" + Guid.NewGuid()
                                    .ToString("N") + ".png"
                            );
                            try
                            {
                                bitmap.Save(temporary, PngBitmapEncoderOptions.Default);
                                await api.CallAsync<bool>(
                                    "workspace.instances.saveIcon",
                                    profile.Id,
                                    new
                                    {
                                        path = temporary
                                    },
                                    ct
                                );
                                await ReloadAsync(profile.Id, ct);
                            }
                            finally
                            {
                                if (File.Exists(temporary))
                                {
                                    File.Delete(temporary);
                                }
                            }

                            changed?.Invoke(profile.Id);
                        }
                    );
                }
            )
        );
        actions.Children.Add(
            Button(
                "恢复默认图标",
                () => Run(
                    async ct =>
                    {
                        await api.CallAsync<bool>("workspace.instances.removeIcon", profile.Id, ct: ct);
                        await ReloadAsync(profile.Id, ct);
                        changed?.Invoke(profile.Id);
                    }
                )
            )
        );
        actions.Children.Add(
            Button(
                "调整加载器",
                async () =>
                {
                    await ComponentManagerWindow.OpenAsync(
                        this,
                        components(profile.Id),
                        new ComponentSource(profile.Id, profile.Name, profile.GameVersion, profile.Loader),
                        id => changed?.Invoke(id)
                    );
                    await Run(
                        ct => ReloadAsync(profile.Id, ct)
                    );
                }
            )
        );
        actions.Children.Add(
            Button(
                "打开目录",
                () =>
                {
                    OpenFolder(profile.GameDirectory);
                    return Task.CompletedTask;
                }
            )
        );
        actions.Children.Add(
            Button(
                "复制实例",
                () => Run(
                    async ct =>
                    {
                        var copy = await api.CallAsync<GameProfile>(
                            "workspace.instances.copy",
                            profile.Id,
                            new
                            {
                                name = profile.Name + " · 副本"
                            },
                            ct
                        );
                        await ReloadAsync(copy.Id, ct);
                        changed?.Invoke(copy.Id);
                    }
                )
            )
        );
        actions.Children.Add(
            Button(
                "校验并修复",
                () => Run(
                    ct => api.CallAsync<bool>(
                        "workspace.instances.repair",
                        profile.Id,
                        ct: ct,
                        progress: Progress()
                    )
                )
            )
        );
        actions.Children.Add(
            Button(
                "备份 ZIP",
                async () =>
                {
                    var file = await SaveFile(profile.Id + "-backup.zip", "实例备份", "*.zip");
                    if (file is not null)
                        await Run(
                            ct => api.ExportAsync(
                                "workspace.instances.backup",
                                profile.Id,
                                new { },
                                file,
                                ct
                            )
                        );
                }
            )
        );
        actions.Children.Add(
            Button(
                "移入回收站",
                async () =>
                {
                    var shared = snapshot.Items.Single(
                        x => x.Profile.Id == profile.Id
                    )
                        .Shared;
                    if (!await Confirm(
                        "移入回收站",
                        shared
                            ? "此实例使用共享游戏目录。仅移除列表记录，存档和其他实例需要的共享资源全部保留，可在回收站恢复。"
                            : "将移走此实例及其存档、模组和配置。可在回收站完整恢复。"
                    ))
                        return;
                    await Run(
                        async ct =>
                        {
                            await api.CallAsync<DeletedInstance>("workspace.instances.trash", profile.Id, ct: ct);
                            await ReloadAsync(null, ct);
                            changed?.Invoke(null);
                        }
                    );
                }
            )
        );
        details.Children.Add(actions);
    }

    private void ShowLaunchSettings(GameProfile? profile)
    {
        details.Children.Clear();
        var store = manager.Settings;
        var settings = profile is null
            ? store.Global
            : store.Effective(profile.GameDirectory);
        details.Children.Add(
            new TextBlock
            {
                Text = profile is null
                    ? "全局启动设置"
                    : profile.Name + " · 启动设置",
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        );
        var fields = new StackPanel
        {
            Spacing = 8
        };
        var overrides = new Dictionary<string, CheckBox>();
        var active = profile is null
            ? typeof(LaunchSettings).GetProperties()
                .Select(
                    x => x.Name
                )
                .ToArray()
            : store.Patch(profile.GameDirectory)?.Fields ?? [];
        void AddField(string label, string key, Control editor)
        {
            editor.Name = "Launch_" + key;
            var choice = new CheckBox
            {
                Content = "自定义",
                Name = "Override_" + key,
                IsChecked = active.Contains(key),
                IsVisible = profile is not null
            };
            overrides[key] = choice;
            editor.IsEnabled = profile is null || choice.IsChecked == true;
            choice.IsCheckedChanged += (_, _) =>
            {
                editor.IsEnabled = choice.IsChecked == true;
                if (choice.IsChecked != true)
                {
                    var global = Newtonsoft.Json.Linq.JObject.FromObject(store.Global)[key];
                    if (editor is TextBox box)
                        box.Text = global?.ToString();
                    else if (editor is CheckBox check)
                        check.IsChecked = (bool? )global;
                }
            };
            var heading = new DockPanel();
            DockPanel.SetDock(choice, Dock.Right);
            heading.Children.Add(choice);
            heading.Children.Add(
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center
                }
            );
            fields.Children.Add(heading);
            fields.Children.Add(editor);
        }

        TextBox Field(string label, string key, string value)
        {
            var box = new TextBox
            {
                Text = value
            };
            AddField(label, key, box);
            return box;
        }

        if (profile is not null)
        {
            details.Children.Add(
                new TextBlock
                {
                    Text = "未勾选的项目随全局设置变化。",
                    TextWrapping = TextWrapping.Wrap
                }
            );
            details.Children.Add(
                Button(
                    "全部继承全局",
                    () =>
                    {
                        foreach (var box in overrides.Values)
                            box.IsChecked = false;
                        return Task.CompletedTask;
                    }
                )
            );
        }

        var minimum = Field(
            "最小内存（MiB）",
            nameof(LaunchSettings.MinimumMemoryMb),
            settings.MinimumMemoryMb.ToString()
        );
        var maximum = Field(
            "最大内存（MiB）",
            nameof(LaunchSettings.MaximumMemoryMb),
            settings.MaximumMemoryMb.ToString()
        );
        var width = Field(
            "窗口宽度",
            nameof(LaunchSettings.Width),
            settings.Width.ToString()
        );
        var height = Field(
            "窗口高度",
            nameof(LaunchSettings.Height),
            settings.Height.ToString()
        );
        var fullscreen = new CheckBox
        {
            Content = "全屏",
            IsChecked = settings.Fullscreen
        };
        AddField(
            "显示模式",
            nameof(LaunchSettings.Fullscreen),
            fullscreen
        );
        var automatic = new CheckBox
        {
            Content = "自动配置",
            IsChecked = settings.AutomaticMemory
        };
        AddField(
            "自动内存",
            nameof(LaunchSettings.AutomaticMemory),
            automatic
        );
        var info = Field(
            "自定义信息",
            nameof(LaunchSettings.VersionInformation),
            settings.VersionInformation
        );
        var gcMode = Field(
            "内存管理：default / g1 / zgc / generational",
            nameof(LaunchSettings.GarbageCollector),
            settings.GarbageCollector
        );
        var java = Field(
            "Java 可执行文件（留空自动匹配）",
            nameof(LaunchSettings.JavaExecutable),
            settings.JavaExecutable
        );
        fields.Children.Add(
            Button(
                "选择并检查 Java",
                async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new FilePickerOpenOptions
                        {
                            Title = "选择 bin/java",
                            AllowMultiple = false
                        }
                    );
                    if (files.FirstOrDefault()?.TryGetLocalPath()is string path)
                        await Run(
                            async ct =>
                            {
                                var runtime = await api.CallAsync<JavaRuntime>(
                                    "workspace.java.inspect",
                                    null,
                                    new
                                    {
                                        path
                                    },
                                    ct
                                );
                                overrides[nameof(LaunchSettings.JavaExecutable)].IsChecked = true;
                                java.Text = path;
                                status.Text = $"Java {runtime.MajorVersion} · {runtime.Architecture}";
                            }
                        );
                }
            )
        );
        fields.Children.Add(
            Button(
                "查找已安装 Java",
                async () =>
                {
                    IReadOnlyList<DiscoveredJava> runtimes = [];
                    await Run(
                        async ct => runtimes = await api.CallAsync<DiscoveredJava[]>(
                            "workspace.java.discover",
                            null,
                            new
                            {
                                paths = string.IsNullOrWhiteSpace(java.Text)
                                    ? Array.Empty<string>()
                                    : new[]
                                    {
                                        java.Text!
                                    }
                            },
                            ct
                        )
                    );
                    if (runtimes.Count == 0)
                    {
                        status.Text = "没有发现可用 Java，可手动选择，或留空由启动器下载匹配版本。";
                        return;
                    }

                    var picker = new WorkspaceWindow
                    {
                        Title = "选择已安装 Java",
                        Width = 720,
                        Height = 460,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner
                    };
                    var items = new ListBox
                    {
                        ItemsSource = runtimes
                    };
                    var root = new DockPanel
                    {
                        Margin = new Thickness(20)
                    };
                    var apply = new Button
                    {
                        Content = "使用所选 Java",
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    DockPanel.SetDock(apply, Dock.Bottom);
                    root.Children.Add(apply);
                    root.Children.Add(items);
                    picker.Content = root;
                    apply.Click += (_, _) =>
                    {
                        if (items.SelectedItem is DiscoveredJava { Runtime: not null } selected)
                        {
                            overrides[nameof(LaunchSettings.JavaExecutable)].IsChecked = true;
                            java.Text = selected.Executable;
                            picker.Close();
                        }
                    };
                    await picker.ShowDialog(this);
                }
            )
        );
        // 实例级覆盖与全局设置使用相同字段；取消自定义即恢复继承。
        var disableUnsafe = new CheckBox
        {
            IsChecked = settings.DisableLwjglUnsafeAgent
        };
        AddField(
            "禁用 LWJGL Unsafe Agent",
            nameof(LaunchSettings.DisableLwjglUnsafeAgent),
            disableUnsafe
        );
        var windowTitle = Field(
            "游戏窗口标题",
            nameof(LaunchSettings.GameWindowTitle),
            settings.GameWindowTitle
        );
        var windowMode = Field(
            "窗口模式（default / custom / launcher / maximized）",
            nameof(LaunchSettings.WindowMode),
            settings.WindowMode
        );
        var priority = Field(
            "进程优先级（normal / low / high）",
            nameof(LaunchSettings.ProcessPriority),
            settings.ProcessPriority
        );
        var jvm = Field(
            "附加 JVM 参数",
            nameof(LaunchSettings.JvmArguments),
            settings.JvmArguments
        );
        var game = Field(
            "附加游戏参数",
            nameof(LaunchSettings.GameArguments),
            settings.GameArguments
        );
        var server = Field(
            "启动后加入服务器（可选）",
            nameof(LaunchSettings.ServerAddress),
            settings.ServerAddress
        );
        var world = Field(
            "快速进入世界（存档文件夹名称，可选）",
            nameof(LaunchSettings.QuickPlayWorld),
            settings.QuickPlayWorld
        );
        details.Children.Add(fields);
        details.Children.Add(
            Button(
                "保存启动设置",
                () => Run(
                    async ct =>
                    {
                        {
                            if (!int.TryParse(minimum.Text, out var min) || !int.TryParse(maximum.Text, out var max) || !int.TryParse(width.Text, out var w) || !int.TryParse(height.Text, out var h))
                                throw new InvalidDataException("内存和窗口尺寸需要填写整数。");
                            var value = new LaunchSettings
                            {
                                DisableLwjglUnsafeAgent = disableUnsafe.IsChecked == true,
                                GameWindowTitle = windowTitle.Text ?? "",
                                WindowMode = windowMode.Text ?? "custom",
                                ProcessPriority = priority.Text ?? "normal",
                                AutomaticMemory = automatic.IsChecked == true,
                                VersionInformation = info.Text ?? "",
                                GarbageCollector = gcMode.Text ?? "default",
                                MinimumMemoryMb = min,
                                MaximumMemoryMb = max,
                                Width = w,
                                Height = h,
                                Fullscreen = fullscreen.IsChecked == true,
                                JavaExecutable = java.Text?.Trim() ?? "",
                                JvmArguments = jvm.Text ?? "",
                                GameArguments = game.Text ?? "",
                                ServerAddress = server.Text?.Trim() ?? "",
                                QuickPlayWorld = world.Text?.Trim() ?? ""
                            };
                            await api.CallAsync<bool>(
                                "workspace.instances.saveSettings",
                                profile?.Id,
                                new
                                {
                                    value,
                                    fields = overrides.Where(
                                        x => x.Value.IsChecked == true
                                    )
                                        .Select(
                                            x => x.Key
                                        )
                                        .ToArray()
                                },
                                ct
                            );
                            await ReloadAsync(profile?.Id, ct);
                        }

                        status.Text = "启动设置已保存，下次启动生效";
                    }
                )
            )
        );
    }

    private async Task ImportDirectory()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择游戏目录或 PCL Mac 实例",
                AllowMultiple = false
            }
        );
        if (folders.FirstOrDefault()?.TryGetLocalPath()is not string folder)
            return;
        InstanceScan? scan = null;
        await Run(
            async ct => scan = await api.CallAsync<InstanceScan>(
                "workspace.instances.scan",
                null,
                new
                {
                    path = folder
                },
                ct
            )
        );
        if (scan is null)
        {
            return;
        }

        var candidates = scan.Items;
        var chooser = new ListBox
        {
            ItemsSource = candidates,
            MinHeight = 200
        };
        chooser.SelectedIndex = candidates.Length > 0
            ? 0
            : -1;
        var dialog = new WorkspaceWindow
        {
            Title = "选择导入版本",
            Width = 620,
            Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel
        {
            Margin = new Thickness(20)
        };
        var import = new Button
        {
            Content = "复制并校验导入",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(import, Dock.Bottom);
        panel.Children.Add(import);
        panel.Children.Add(chooser);
        dialog.Content = panel;
        InstanceImportCandidate? selected = null;
        import.Click += (_, _) =>
        {
            if (chooser.SelectedItem is InstanceImportCandidate { Supported: true } item)
            {
                selected = item;
                dialog.Close();
            }
        };
        if (candidates.Length == 0)
            status.Text = "目录中没有识别到版本；请选择含 versions 的游戏目录或含 profile.json 的本启动器实例。";
        await dialog.ShowDialog(this);
        if (selected is not null)
            await Run(
                async ct =>
                {
                    var result = await api.CallAsync<GameProfile>(
                        "workspace.instances.import",
                        null,
                        new
                        {
                            selection = scan.Handle + ":" + Array.IndexOf(candidates, selected)
                        },
                        ct,
                        Progress()
                    );
                    await ReloadAsync(result.Id, ct);
                    changed?.Invoke(result.Id);
                }
            );
    }

    public async Task VerifyBackupFileAsync(string file)
    {
        BackupVerification? verified = null;
        await Run(
            async ct => verified = await api.CallAsync<BackupVerification>(
                "workspace.instances.verifyBackup",
                null,
                new
                {
                    path = file
                },
                ct
            )
        );
        if (verified is not null)
            status.Text = verified.ToString();
    }

    private async Task VerifyBackup()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new()
            {
                Title = "校验实例备份",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("ZIP 备份")
                    {
                        Patterns = [
                            "*.zip"
                        ]
                    }
                ]
            }
        );
        if (files.FirstOrDefault()?.TryGetLocalPath()is string file)
            await VerifyBackupFileAsync(file);
    }

    private async Task RestoreBackup()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择实例备份",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("ZIP 备份")
                    {
                        Patterns = [
                            "*.zip"
                        ]
                    }
                ]
            }
        );
        if (files.FirstOrDefault()?.TryGetLocalPath()is string file)
            await Run(
                async ct =>
                {
                    var restored = await api.CallAsync<GameProfile>(
                        "workspace.instances.restoreBackup",
                        null,
                        new
                        {
                            path = file
                        },
                        ct,
                        Progress()
                    );
                    await ReloadAsync(restored.Id, ct);
                    changed?.Invoke(restored.Id);
                }
            );
    }

    private async Task ShowTrash()
    {
        details.Children.Clear();
        details.Children.Add(
            new TextBlock
            {
                Text = "回收站",
                FontSize = 22,
                FontWeight = FontWeight.SemiBold
            }
        );
        DeletedInstance[] entries = [];
        await Run(
            async ct => entries = await api.CallAsync<DeletedInstance[]>("workspace.instances.listTrash", null, ct: ct)
        );
        if (entries.Length == 0)
            details.Children.Add(
                new TextBlock
                {
                    Text = "回收站为空"
                }
            );
        foreach (var entry in entries)
        {
            details.Children.Add(
                new TextBlock
                {
                    Text = entry.Name + " · " + entry.DeletedAt.ToLocalTime()
                        .ToString("g")
                }
            );
            details.Children.Add(
                Button(
                    "恢复",
                    () => Run(
                        async ct =>
                        {
                            var profile = await api.CallAsync<GameProfile>(
                                "workspace.instances.restoreTrash",
                                null,
                                new
                                {
                                    id = entry.Id
                                },
                                ct
                            );
                            await ReloadAsync(profile.Id, ct);
                            changed?.Invoke(profile.Id);
                        }
                    )
                )
            );
        }
    }

    private IProgress<VersionInstallProgress> Progress() => new Progress<VersionInstallProgress>(
        p => status.Text = p.Stage + (p.Total > 0
            ? $" · {p.Completed}/{p.Total}"
            : "")
    );
    private async Task Run(Func<CancellationToken, Task> task)
    {
        if (operation is not null)
            return;
        operation = new();
        cancel.IsVisible = true;
        list.IsEnabled = details.IsEnabled = false;
        status.Text = "正在处理…";
        try
        {
            await task(operation.Token);
            status.Text = "操作完成";
        }
        catch (OperationCanceledException)
        {
            status.Text = "已请求取消；请在任务记录查看最终状态";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            operation.Dispose();
            operation = null;
            cancel.IsVisible = false;
            list.IsEnabled = details.IsEnabled = true;
        }
    }

    private async Task<string?> SaveFile(string suggested, string name, string pattern) => (await StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions
        {
            SuggestedFileName = suggested,
            FileTypeChoices = [
                new FilePickerFileType(name)
                {
                    Patterns = [
                        pattern
                    ]
                }
            ]
        }
    ))?.TryGetLocalPath();
    private async Task<bool> Confirm(string title, string text)
    {
        var window = new WorkspaceWindow
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var accepted = false;
        var panel = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap
            }
        );
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var yes = new Button
        {
            Content = "移入回收站"
        };
        var no = new Button
        {
            Content = "取消"
        };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        panel.Children.Add(buttons);
        window.Content = panel;
        yes.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        no.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
        return accepted;
    }

    private static void OpenFolder(string folder)
    {
        var info = new ProcessStartInfo(
            OperatingSystem.IsMacOS()
                ? "open"
                : "xdg-open"
        )
        {
            UseShellExecute = false
        };
        info.ArgumentList.Add(folder);
        Process.Start(info);
    }
}
