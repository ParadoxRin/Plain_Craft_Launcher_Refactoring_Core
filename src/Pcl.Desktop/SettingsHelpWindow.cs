using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 设置和个性化：读取、校验并应用用户配置，更新界面或处理本地文件入口，保存可恢复设置。
public sealed class SettingsHelpWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly string rootPath;
    private readonly Action<AppearanceSettings> apply;
    private readonly Action<string> open;
    private CancellationTokenSource? operation;
    public bool Busy => operation is not null;

    public SettingsHelpWindow(
        IWorkspaceOperations operations,
        string rootPath,
        Action<AppearanceSettings> apply,
        Action<string> open
    )
    {
        api = new(operations);
        this.rootPath = rootPath;
        this.apply = apply;
        this.open = open;
        Content = new TextBlock
        {
            Text = "正在读取设置…"
        };
        Opened += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            operation = new();
            using var viewActivity = ViewLifetime.Begin(operation.Cancel);
            try
            {
                await BuildAsync();
            }
            catch (Exception ex)
            {
                Content = new TextBlock
                {
                    Text = ex.Message
                };
            }
            finally
            {
                operation.Dispose();
                operation = null;
            }
        };
    }

    // 页面草稿来自 Host 快照；保存、导入、导出和启动前程序均经过同一任务通道。
    private async Task BuildAsync()
    {
        Title = "设置与帮助";
        Width = 760;
        Height = 720;
        MinWidth = 600;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var snapshot = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null, ct: operation!.Token);
        var current = snapshot.Appearance;
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var tabs = new TabControl();
        var root = new DockPanel();
        status.Margin = new Thickness(24, 8);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(tabs);
        Content = root;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        TextBox Field(string label, string text)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = label
                }
            );
            var input = new TextBox
            {
                Text = text
            };
            panel.Children.Add(input);
            return input;
        }

        var theme = new ComboBox
        {
            ItemsSource = new[]
            {
                "跟随系统",
                "浅色",
                "深色"
            },
            SelectedIndex = current.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(theme);
        var title = Field("窗口标题", current.Title);
        var color = Field("主题颜色", current.Accent);
        var background = Field("背景图片", current.Background);
        Button Button(string label, Func<Task> work)
        {
            var button = new Button
            {
                Content = label
            };
            button.Click += async (_, _) =>
            {
                if (Busy || ViewClosing)
                {
                    return;
                }

                operation = new();
                using var viewActivity = ViewLifetime.Begin(operation.Cancel);
                if (Content is Control view)
                {
                    view.IsEnabled = false;
                }

                button.IsEnabled = false;
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    status.Text = WorkspaceErrors.Describe(ex);
                }
                finally
                {
                    operation.Dispose();
                    operation = null;
                    if (Content is Control completedView)
                    {
                        completedView.IsEnabled = true;
                    }

                    button.IsEnabled = true;
                }
            };
            return button;
        }

        async Task Pick(TextBox target, string title)
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new()
                {
                    Title = title
                }
            );
            if (files.FirstOrDefault()?.TryGetLocalPath()is string path)
                target.Text = path;
        }

        panel.Children.Add(
            Button(
                "选择图片",
                () => Pick(background, "选择本地背景图片")
            )
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "背景透明度"
            }
        );
        var opacity = new Slider
        {
            Minimum = 0,
            Maximum = .5,
            Value = current.BackgroundOpacity
        };
        panel.Children.Add(opacity);
        var music = Field("本地音乐", current.Music);
        panel.Children.Add(
            Button(
                "选择音乐",
                () => Pick(music, "选择本地音频")
            )
        );
        var volume = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = current.MusicVolume
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "音乐音量"
            }
        );
        panel.Children.Add(volume);
        var pause = new CheckBox
        {
            Content = "游戏启动时停止音乐，退出后重新播放",
            IsChecked = current.PauseMusicInGame
        };
        panel.Children.Add(pause);
        var chinese = new CheckBox
        {
            Content = "首次启动自动选择中文（保留已有游戏语言）",
            IsChecked = current.FirstLaunchChinese
        };
        panel.Children.Add(chinese);
        var hide = new CheckBox
        {
            Content = "游戏运行时隐藏启动器",
            IsChecked = current.HideLauncherInGame
        };
        panel.Children.Add(hide);
        var home = Field("自定义主页（本地 JSON 或 HTTPS）", current.HomeDocument);
        var hidden = new Dictionary<string, CheckBox>();
        var nav = new[]
        {
            ("NavMods", "模组"),
            ("NavShaders", "光影"),
            ("NavPacks", "资源包"),
            ("NavDataPacks", "数据包"),
            ("NavModpacks", "整合包"),
            ("NavCurseForge", "CurseForge"),
            ("NavManaged", "已安装"),
            ("NavLocal", "本地资源"),
            ("NavPackTools", "整合包管理")
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "显示的导航项目"
            }
        );
        var choices = new WrapPanel();
        foreach (var(id, text)in nav)
        {
            var check = new CheckBox
            {
                Content = text,
                IsChecked = !current.HiddenNavigation.Contains(id),
                Margin = new Thickness(0, 0, 14, 8)
            };
            hidden[id] = check;
            choices.Children.Add(check);
        }

        panel.Children.Add(choices);
        panel.Children.Add(
            Button(
                "保存并应用",
                async () =>
                {
                    var next = current with
                    {
                        Theme = theme.SelectedIndex switch
                        {
                            1 => "light",
                            2 => "dark",
                            _ => "system"
                        },
                        Title = title.Text ?? "",
                        Accent = color.Text ?? "",
                        Background = background.Text ?? "",
                        BackgroundOpacity = opacity.Value,
                        Music = music.Text ?? "",
                        MusicVolume = volume.Value,
                        PauseMusicInGame = pause.IsChecked == true,
                        HideLauncherInGame = hide.IsChecked == true,
                        FirstLaunchChinese = chinese.IsChecked == true,
                        HomeDocument = home.Text ?? "",
                        HiddenNavigation = hidden.Where(
                            x => x.Value.IsChecked != true
                        )
                            .Select(
                                x => x.Key
                            )
                            .ToArray()
                    };
                    await api.CallAsync<SettingsSnapshot>(
                        "workspace.settings.save",
                        null,
                        new
                        {
                            changes = new
                            {
                                Appearance = next
                            },
                            expected = new
                            {
                                Appearance = current
                            }
                        },
                        operation!.Token
                    );
                    apply(next);
                    current = next;
                    status.Text = "设置已保存";
                }
            )
        );
        var system = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14
        };
        system.Children.Add(
            new TextBlock
            {
                Text = "可移植设置仅含外观和通用启动选项。账户、文件路径、服务器与执行参数不导出。导入会先备份原设置。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        system.Children.Add(
            Button(
                "导出设置",
                async () =>
                {
                    var file = await StorageProvider.SaveFilePickerAsync(
                        new()
                        {
                            Title = "导出设置",
                            SuggestedFileName = "PCL-Mac-settings.json"
                        }
                    );
                    if (file?.TryGetLocalPath()is string path)
                    {
                        await api.ExportAsync(
                            "workspace.settings.export",
                            null,
                            new { },
                            path,
                            operation!.Token
                        );
                        status.Text = "设置已导出";
                    }
                }
            )
        );
        system.Children.Add(
            Button(
                "导入设置",
                async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new()
                        {
                            Title = "导入设置"
                        }
                    );
                    if (files.FirstOrDefault()?.TryGetLocalPath()is string path)
                    {
                        var restored = await api.CallAsync<SettingsSnapshot>(
                            "workspace.settings.import",
                            null,
                            new
                            {
                                path
                            },
                            operation!.Token
                        );
                        apply(restored.Appearance);
                        await BuildAsync();
                    }
                }
            )
        );
        system.Children.Add(
            Button(
                "恢复默认外观和全部导航",
                async () =>
                {
                    await api.CallAsync<SettingsSnapshot>(
                        "workspace.settings.save",
                        null,
                        new
                        {
                            changes = new
                            {
                                Appearance = new AppearanceSettings()
                            }
                        },
                        operation!.Token
                    );
                    apply(
                        new()
                    );
                    await BuildAsync();
                }
            )
        );
        system.Children.Add(
            Button(
                "设置备份文件夹",
                async () =>
                {
                    open(
                        await api.CallAsync<string>("workspace.settings.backups", null, ct: operation!.Token)
                    );
                }
            )
        );
        system.Children.Add(
            Button(
                "打开启动器数据文件夹",
                async () =>
                {
                    open(rootPath);
                }
            )
        );
        var hook = snapshot.Hook;
        var hookEnabled = new CheckBox
        {
            Content = "启动游戏前执行本地程序",
            IsChecked = hook.Enabled
        };
        var hookExe = new TextBox
        {
            Text = hook.Executable,
            PlaceholderText = "可执行文件完整路径"
        };
        var hookArgs = new TextBox
        {
            Text = hook.Arguments,
            PlaceholderText = "参数（按引号分词，不进行 Shell 展开）"
        };
        var hookWait = new CheckBox
        {
            Content = "等待程序成功退出后再启动",
            IsChecked = hook.Wait
        };
        var hookTimeout = new TextBox
        {
            Text = hook.TimeoutSeconds.ToString(),
            PlaceholderText = "等待超时秒数"
        };
        system.Children.Add(
            new Separator()
        );
        system.Children.Add(
            new TextBlock
            {
                Text = "启动前程序",
                FontSize = 20
            }
        );
        foreach (var control in new Control[]
        {
            hookEnabled,
            hookExe,
            hookArgs,
            hookWait,
            hookTimeout
        }

        )
            system.Children.Add(control);
        system.Children.Add(
            Button(
                "保存启动前程序",
                async () =>
                {
                    var nextHook = new LaunchHookSettings(
                        hookEnabled.IsChecked == true,
                        hookExe.Text ?? "",
                        hookArgs.Text ?? "",
                        hookWait.IsChecked == true,
                        int.Parse(hookTimeout.Text ?? "60")
                    );
                    await api.CallAsync<SettingsSnapshot>(
                        "workspace.settings.save",
                        null,
                        new
                        {
                            changes = new
                            {
                                Hook = nextHook
                            },
                            expected = new
                            {
                                Hook = hook
                            }
                        },
                        operation!.Token
                    );
                    hook = nextHook;
                    status.Text = "已保存，仅在下次启动游戏时执行；不随设置或整合包导入导出。";
                }
            )
        );
        system.Children.Add(
            new TextBlock
            {
                Text = "当前语言：简体中文\n更新：当前为本地构建，尚未配置签名更新源。\n完整性修复和游戏目录管理位于“实例管理”。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var help = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        var search = new TextBox
        {
            PlaceholderText = "搜索帮助内容"
        };
        var list = new ListBox
        {
            Height = 200
        };
        var body = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 340
        };
        help.Children.Add(search);
        help.Children.Add(list);
        help.Children.Add(body);
        var bundled = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "Resources")
        );
        var local = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../docs")
        );
        var docs = Directory.Exists(bundled)
            ? bundled
            : local;
        void Search()
        {
            list.ItemsSource = HelpFiles(docs, search.Text ?? "")
                .Select(
                    x => new HelpItem(x)
                )
                .ToArray();
        }

        search.TextChanged += (_, _) => Search();
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is HelpItem item)
                body.Text = File.ReadAllText(item.Path);
        };
        Search();
        tabs.ItemsSource = new[]
        {
            new TabItem
            {
                Header = "外观",
                Content = new ScrollViewer
                {
                    Content = panel
                }
            },
            new TabItem
            {
                Header = "系统",
                Content = new ScrollViewer
                {
                    Content = system
                }
            },
            new TabItem
            {
                Header = "帮助",
                Content = new ScrollViewer
                {
                    Content = help
                }
            }
        };
    }

    // 帮助文本是随 Shell 打包的静态展示资源，不访问游戏目录。
    private static string[] HelpFiles(string folder, string query) => Directory.Exists(folder)
        ? Directory.GetFiles(folder, "*.md")
            .Where(
                p => Path.GetFileName(p)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray()
        : [];
    private sealed record HelpItem(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
    }
}
