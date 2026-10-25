using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Newtonsoft.Json.Linq;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 原版设置卡片直接读写既有服务。编辑使用草稿，保存前统一校验，不以保存任意键值冒充功能实现。
public partial class MainWindow
{
    private void ApplyClassicAppearance(AppearanceSettings value)
    {
        if (classicHeader is null || classicBrand is null)
        {
            return;
        }

        classicHeader.Background = Brush.Parse(value.Accent);
        ToolTip.SetTip(classicBrand, value.Title);
        classicBrand.Text = value.TitleMode switch
        {
            "none" => "",
            "text" => value.Title,
            _ => "PCL"
        };
        UpdateClassicTabs();
        BuildClassicNavigation(classicSection);
        ApplyLocalAppearance(value);
    }

    private async Task<Control> BuildWindowsSettingsAsync(string page)
    {
        var api = new Pcl.Client.WorkspaceApi(
            Bootstrap.WorkspacePages.Operations(this, service)
        );
        var snapshot = await api.CallAsync<System.Text.Json.JsonElement>("workspace.settings.read", null);
        var saved = JObject.Parse(
            snapshot.GetRawText()
        );
        var launch = (JObject)saved["Launch"]!;
        var look = (JObject)saved["Appearance"]!;
        var network = (JObject)saved["Network"]!;
        var hook = (JObject)saved["Hook"]!;
        var originalLaunch = launch.DeepClone();
        var originalLook = look.DeepClone();
        var originalNetwork = network.DeepClone();
        var originalHook = hook.DeepClone();
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 15
        };
        var status = new TextBlock
        {
            Text = "修改后点击保存。全局启动设置由未覆盖该字段的实例继承。",
            TextWrapping = TextWrapping.Wrap,
            Name = "ClassicSettingsStatus"
        };
        var save = new Button
        {
            Content = "保存设置",
            Name = "ClassicSettingsSave"
        };
        panel.Children.Add(
            ClassicCard(
                "设置",
                new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        status,
                        save
                    }
                }
            )
        );
        StackPanel Card(string title)
        {
            var fields = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(16)
            };
            panel.Children.Add(
                new Expander
                {
                    Header = title,
                    IsExpanded = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.White,
                    Content = fields
                }
            );
            return fields;
        }

        void Note(StackPanel card, string text)
        {
            card.Children.Add(
                new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush.Parse("#596A7B"),
                    FontSize = 12
                }
            );
        }

        void Row(StackPanel card, string label, Control control)
        {
            var row = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("180,*"),
                ColumnSpacing = 12
            };
            row.Children.Add(
                new TextBlock
                {
                    Text = label,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            );
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            card.Children.Add(row);
        }

        TextBox Text(StackPanel card, string label, JObject draft, string key)
        {
            var input = new TextBox
            {
                Text = (string? )draft[key] ?? "",
                Tag = key
            };
            // 直接监听属性变更，确保切换后立刻保存也读取到最新值。
            input.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    draft[key] = input.Text ?? "";
                }
            };
            // 同一字段的预设选择和文本输入保持同步，避免颜色下拉与自定义值显示不一致。
            draft.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == key && input.Text != (string? )draft[key])
                {
                    input.Text = (string? )draft[key] ?? "";
                }
            };
            Row(card, label, input);
            return input;
        }

        void Number(
            StackPanel card,
            string label,
            JObject draft,
            string key,
            decimal min,
            decimal max,
            decimal step = 1
        )
        {
            var input = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Increment = step,
                Value = (decimal? )draft[key] ?? min,
                Tag = key
            };
            input.ValueChanged += (_, _) => draft[key] = input.Value ?? min;
            Row(card, label, input);
        }

        void Toggle(StackPanel card, string label, JObject draft, string key)
        {
            var input = new CheckBox
            {
                Content = label,
                IsChecked = (bool? )draft[key] ?? false,
                Tag = key
            };
            input.IsCheckedChanged += (_, _) => draft[key] = input.IsChecked == true;
            card.Children.Add(input);
        }

        void Choice(
            StackPanel card,
            string label,
            JObject draft,
            string key,
            string[] labels,
            string[] values
        )
        {
            var input = new ComboBox
            {
                ItemsSource = labels,
                SelectedIndex = Math.Max(
                    0,
                    Array.IndexOf(values, (string? )draft[key])
                ),
                Tag = key
            };
            input.SelectionChanged += (_, _) =>
            {
                if (input.SelectedIndex >= 0)
                {
                    draft[key] = values[input.SelectedIndex];
                }
            };
            Row(card, label, input);
        }

        void Action(StackPanel card, string label, Func<Task> action)
        {
            var button = new Button
            {
                Content = label
            };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    status.Text = Pcl.Client.WorkspaceErrors.Describe(ex);
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            card.Children.Add(button);
        }

        async Task Pick(TextBox input, string title)
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                }
            );
            if (files.FirstOrDefault()?.TryGetLocalPath()is { } file)
            {
                input.Text = file;
            }
        }

        // 所有可写配置先验证，再使用各服务的原子 JSON 保存。页面刷新重新读取磁盘，错误不会被成功提示覆盖。
        save.Click += async (_, _) =>
        {
            try
            {
                save.IsEnabled = false;
                var changes = new JObject();
                var expected = new JObject();
                void Changed(string name, JToken value, JToken original)
                {
                    if (!JToken.DeepEquals(value, original))
                    {
                        changes[name] = value.DeepClone();
                        expected[name] = original.DeepClone();
                    }
                }

                Changed("Launch", launch, originalLaunch);
                Changed("Appearance", look, originalLook);
                Changed("Network", network, originalNetwork);
                Changed("Hook", hook, originalHook);
                // JsonElement avoids serializing Newtonsoft tokens as enumerable CLR objects across the Contract boundary.
                var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    new JObject
                    {
                        ["changes"] = changes,
                        ["expected"] = expected
                    }.ToString()
                );
                await api.CallAsync<System.Text.Json.JsonElement>("workspace.settings.save", null, payload);
                originalLaunch = launch.DeepClone();
                originalLook = look.DeepClone();
                originalNetwork = network.DeepClone();
                originalHook = hook.DeepClone();
                ApplyAppearance(
                    look.ToObject<AppearanceSettings>()!
                );
                await RefreshClassicHomeAsync();
                status.Text = "已保存并应用。启动设置在下次游戏启动生效；代理在重开应用后生效。";
            }
            catch (Exception ex)
            {
                status.Text = "保存失败：" + Pcl.Client.WorkspaceErrors.Describe(ex);
            }
            finally
            {
                save.IsEnabled = true;
            }
        };
        if (page == "PageSetupLaunch")
        {
            var general = Card("启动选项");
            Text(
                general,
                "游戏窗口标题",
                launch,
                nameof(LaunchSettings.GameWindowTitle)
            );
            Choice(
                general,
                "进程优先级",
                launch,
                nameof(LaunchSettings.ProcessPriority),
                [
                    "正常（不提权）",
                    "低（挂机）",
                    "前台优先（macOS）"
                ],
                [
                    "normal",
                    "low",
                    "high"
                ]
            );
            Choice(
                general,
                "启动器可见性",
                look,
                nameof(AppearanceSettings.LauncherVisibility),
                [
                    "兼容旧设置",
                    "游戏启动后立即关闭",
                    "隐藏，游戏退出后关闭",
                    "隐藏，游戏退出后恢复",
                    "最小化",
                    "保持不变"
                ],
                [
                    "legacy",
                    "close",
                    "hide-close",
                    "hide-show",
                    "minimize",
                    "keep"
                ]
            );
            Choice(
                general,
                "游戏窗口模式",
                launch,
                nameof(LaunchSettings.WindowMode),
                [
                    "默认 854×480",
                    "自定义尺寸",
                    "与启动器尺寸一致",
                    "最大化"
                ],
                [
                    "default",
                    "custom",
                    "launcher",
                    "maximized"
                ]
            );
            Note(general, "各实例保持独立目录。标题与最大化由游戏进程内的 Mac 组件应用；高优先级需要系统提权，本版不请求提权。");
            Text(
                general,
                "自定义信息",
                launch,
                nameof(LaunchSettings.VersionInformation)
            );
            Toggle(
                general,
                "游戏启动后隐藏，退出后重新打开",
                look,
                nameof(AppearanceSettings.HideLauncherInGame)
            );
            Toggle(
                general,
                "游戏启动后最小化（隐藏优先）",
                look,
                nameof(AppearanceSettings.MinimizeLauncherInGame)
            );
            Toggle(
                general,
                "游戏全屏",
                launch,
                nameof(LaunchSettings.Fullscreen)
            );
            Number(
                general,
                "游戏窗口宽度",
                launch,
                nameof(LaunchSettings.Width),
                320,
                16384
            );
            Number(
                general,
                "游戏窗口高度",
                launch,
                nameof(LaunchSettings.Height),
                240,
                16384
            );
            var memory = Card("内存分配");
            Toggle(
                memory,
                "自动配置（最多 8 GiB，保留一半运行时可用内存）",
                launch,
                nameof(LaunchSettings.AutomaticMemory)
            );
            Number(
                memory,
                "最小内存 MiB",
                launch,
                nameof(LaunchSettings.MinimumMemoryMb),
                128,
                262144,
                128
            );
            Number(
                memory,
                "最大内存 MiB（自定义）",
                launch,
                nameof(LaunchSettings.MaximumMemoryMb),
                128,
                262144,
                128
            );
            Note(memory, "macOS 管理系统内存；不执行 Windows 工作集清理。最大值不得小于最小值。");
            var skin = Card("离线皮肤");
            Note(skin, "为当前离线账户选择本地 PNG，启动时由已有资源包逻辑应用。微软皮肤在同一管理页处理。");
            Action(
                skin,
                "更改 / 保存 / 重置皮肤",
                async () =>
                {
                    if (accounts is null)
                    {
                        throw new InvalidOperationException("请先添加账户。");
                    }

                    await Bootstrap.WorkspacePages.Skins(this, accounts)
                        .ShowDialog(this);
                }
            );
            var advanced = Card("高级选项");
            Toggle(
                advanced,
                "禁用 LWJGL Unsafe Agent（Java 25+ / LWJGL 3.4.0—3.4.1）",
                launch,
                nameof(LaunchSettings.DisableLwjglUnsafeAgent)
            );
            var java = Text(
                advanced,
                "Java 路径（留空自动匹配）",
                launch,
                nameof(LaunchSettings.JavaExecutable)
            );
            Action(
                advanced,
                "选择 Java",
                () => Pick(java, "选择 Java 可执行文件")
            );
            Action(
                advanced,
                "检查所选 Java",
                async () =>
                {
                    if (string.IsNullOrWhiteSpace(java.Text))
                    {
                        status.Text = "自动匹配已启用；启动时依据 Minecraft 元数据选择版本与架构。";
                        return;
                    }

                    var runtime = await api.CallAsync<Pcl.Core.Contracts.Workspaces.JavaRuntime>(
                        "workspace.java.inspect",
                        null,
                        new
                        {
                            path = java.Text
                        }
                    );
                    status.Text = $"Java {runtime.MajorVersion} · {runtime.Architecture}";
                }
            );
            Text(
                advanced,
                "额外 JVM 参数",
                launch,
                nameof(LaunchSettings.JvmArguments)
            );
            Text(
                advanced,
                "游戏参数",
                launch,
                nameof(LaunchSettings.GameArguments)
            );
            Choice(
                advanced,
                "内存管理",
                launch,
                nameof(LaunchSettings.GarbageCollector),
                [
                    "不指定（Java 默认 / 自定义）",
                    "标准 G1GC",
                    "调优 G1GC（50ms 目标）",
                    "尽量使用 ZGC",
                    "尽量使用分代 ZGC"
                ],
                [
                    "default",
                    "g1",
                    "g1-tuned",
                    "zgc",
                    "generational"
                ]
            );
            Note(
                advanced,
                "ZGC 在 Java 17+ 启用；分代在 Java 21+ 启用，旧 Java 回退 G1。不会向 Java 24+ 传入已移除的 ZGenerational 标志。TLS 证书始终验证；Mac 不使用 Windows Java Launch Wrapper 或显卡注册表。"
            );
            Text(
                advanced,
                "自动加入服务器",
                launch,
                nameof(LaunchSettings.ServerAddress)
            );
            Text(
                advanced,
                "快速进入世界（存档目录名）",
                launch,
                nameof(LaunchSettings.QuickPlayWorld)
            );
            Toggle(
                advanced,
                "启动前执行本地程序",
                hook,
                nameof(LaunchHookSettings.Enabled)
            );
            var executable = Text(
                advanced,
                "可执行文件",
                hook,
                nameof(LaunchHookSettings.Executable)
            );
            Action(
                advanced,
                "选择启动前程序",
                () => Pick(executable, "选择本地可执行文件")
            );
            Text(
                advanced,
                "程序参数（不经 shell）",
                hook,
                nameof(LaunchHookSettings.Arguments)
            );
            Toggle(
                advanced,
                "等待程序结束",
                hook,
                nameof(LaunchHookSettings.Wait)
            );
            Number(
                advanced,
                "等待超时（秒）",
                hook,
                nameof(LaunchHookSettings.TimeoutSeconds),
                1,
                600
            );
        }
        else if (page == "PageSetupUI")
        {
            var basic = Card("基础");
            Choice(
                basic,
                "动态配色",
                look,
                nameof(AppearanceSettings.AccentMode),
                [
                    "静态",
                    "滑稽彩",
                    "欧皇彩"
                ],
                [
                    "static",
                    "rainbow",
                    "aurora"
                ]
            );
            Toggle(
                basic,
                "打开启动器时显示 PCL 图标",
                look,
                nameof(AppearanceSettings.ShowStartupLogo)
            );
            Number(
                basic,
                "动画速度（0 关闭）",
                look,
                nameof(AppearanceSettings.AnimationSpeed),
                0,
                5,
                .25m
            );
            Number(
                basic,
                "窗口不透明度",
                look,
                nameof(AppearanceSettings.WindowOpacity),
                .5m,
                1m,
                .05m
            );
            Choice(
                basic,
                "主题颜色",
                look,
                nameof(AppearanceSettings.Accent),
                [
                    "龙猫蓝",
                    "甜柠青",
                    "小草绿",
                    "菠萝黄",
                    "橡木棕",
                    "玄素黑",
                    "铁杆粉",
                    "神秘紫",
                    "秋仪金",
                    "活跃橙",
                    "跳票红",
                    "极客蓝"
                ],
                [
                    "#126CC5",
                    "#008F9C",
                    "#388E3C",
                    "#B68C00",
                    "#795548",
                    "#303B45",
                    "#C64D87",
                    "#7952B3",
                    "#AD8327",
                    "#DA761C",
                    "#D34C4C",
                    "#3867D6"
                ]
            );
            Text(
                basic,
                "自定义颜色 #RRGGBB",
                look,
                nameof(AppearanceSettings.Accent)
            );
            var background = Card("背景图片");
            var imageFolder = Text(
                background,
                "随机背景文件夹（优先于单图）",
                look,
                nameof(AppearanceSettings.BackgroundDirectory)
            );
            Action(
                background,
                "选择背景文件夹",
                async () =>
                {
                    var chosen = await StorageProvider.OpenFolderPickerAsync(
                        new FolderPickerOpenOptions
                        {
                            Title = "选择背景文件夹"
                        }
                    );
                    if (chosen.FirstOrDefault()?.TryGetLocalPath()is { } selected)
                    {
                        imageFolder.Text = selected;
                    }
                }
            );
            Action(
                background,
                "打开背景文件夹",
                () =>
                {
                    Open(
                        imageFolder.Text?.Length > 0
                            ? imageFolder.Text
                            : paths.Root
                    );
                    return Task.CompletedTask;
                }
            );
            Action(
                background,
                "刷新背景图片",
                async () =>
                {
                    var fresh = await api.CallAsync<System.Text.Json.JsonElement>("workspace.settings.read", null);
                    ApplyAppearance(
                        JObject.Parse(
                            fresh.GetRawText()
                        )["Appearance"]!.ToObject<AppearanceSettings>()!
                    );
                }
            );
            var image = Text(
                background,
                "本地图片",
                look,
                nameof(AppearanceSettings.Background)
            );
            Action(
                background,
                "选择背景图片",
                () => Pick(image, "选择背景图片")
            );
            Action(
                background,
                "清空背景图片",
                () =>
                {
                    image.Text = "";
                    return Task.CompletedTask;
                }
            );
            Choice(
                background,
                "自适应方式",
                look,
                nameof(AppearanceSettings.BackgroundFit),
                [
                    "填满（保持比例）",
                    "完整显示",
                    "拉伸",
                    "居中（原尺寸）",
                    "平铺",
                    "左上",
                    "左中",
                    "左下",
                    "中上",
                    "居中（保持比例）",
                    "中下",
                    "右上",
                    "右中",
                    "右下",
                    "自动（小图平铺）"
                ],
                [
                    "fill",
                    "fit",
                    "stretch",
                    "center",
                    "tile",
                    "top-left",
                    "left",
                    "bottom-left",
                    "top",
                    "middle",
                    "bottom",
                    "top-right",
                    "right",
                    "bottom-right",
                    "auto"
                ]
            );
            Number(
                background,
                "不透明度",
                look,
                nameof(AppearanceSettings.BackgroundOpacity),
                0,
                1,
                .05m
            );
            Number(
                background,
                "背景模糊",
                look,
                nameof(AppearanceSettings.BackgroundBlur),
                0,
                40
            );
            Toggle(
                background,
                "叠加彩色背景",
                look,
                nameof(AppearanceSettings.BackgroundTint)
            );
            var music = Card("背景音乐");
            var folder = Text(
                music,
                "播放列表文件夹（优先于单曲）",
                look,
                nameof(AppearanceSettings.MusicDirectory)
            );
            Action(
                music,
                "选择音乐文件夹",
                async () =>
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(
                        new FolderPickerOpenOptions
                        {
                            Title = "选择音乐文件夹"
                        }
                    );
                    if (folders.FirstOrDefault()?.TryGetLocalPath()is { } path)
                    {
                        folder.Text = path;
                    }
                }
            );
            Action(
                music,
                "打开音乐文件夹",
                () =>
                {
                    Open(
                        folder.Text?.Length > 0
                            ? folder.Text
                            : paths.Root
                    );
                    return Task.CompletedTask;
                }
            );
            Action(
                music,
                "刷新背景音乐",
                () =>
                {
                    musicPlayer.Play(
                        look.ToObject<AppearanceSettings>()!
                    );
                    return Task.CompletedTask;
                }
            );
            Toggle(
                music,
                "随机播放（不连续重复同一首）",
                look,
                nameof(AppearanceSettings.MusicShuffle)
            );
            Choice(
                music,
                "播放时机",
                look,
                nameof(AppearanceSettings.MusicMode),
                [
                    "按自动播放与暂停选项",
                    "游戏启动后播放，退出后停止"
                ],
                [
                    "normal",
                    "game"
                ]
            );
            var audio = Text(
                music,
                "本地音频",
                look,
                nameof(AppearanceSettings.Music)
            );
            Action(
                music,
                "选择背景音乐",
                () => Pick(audio, "选择本地音频")
            );
            Number(
                music,
                "音量",
                look,
                nameof(AppearanceSettings.MusicVolume),
                0,
                1,
                .05m
            );
            Toggle(
                music,
                "打开启动器自动开始播放",
                look,
                nameof(AppearanceSettings.MusicAutoPlay)
            );
            Toggle(
                music,
                "游戏启动后暂停，退出后恢复",
                look,
                nameof(AppearanceSettings.PauseMusicInGame)
            );
            Action(
                music,
                "试听当前音频",
                () =>
                {
                    musicPlayer.Play(
                        look.ToObject<AppearanceSettings>()!
                    );
                    return Task.CompletedTask;
                }
            );
            Action(
                music,
                "停止播放",
                () =>
                {
                    musicPlayer.Stop();
                    return Task.CompletedTask;
                }
            );
            var title = Card("标题栏");
            Choice(
                title,
                "显示方式",
                look,
                nameof(AppearanceSettings.TitleMode),
                [
                    "默认",
                    "无",
                    "文本",
                    "图片"
                ],
                [
                    "default",
                    "none",
                    "text",
                    "image"
                ]
            );
            Text(
                title,
                "标题栏文本 / 窗口标题",
                look,
                nameof(AppearanceSettings.Title)
            );
            var titleImage = Text(
                title,
                "标题栏图片",
                look,
                nameof(AppearanceSettings.TitleImage)
            );
            Action(
                title,
                "更改图片",
                () => Pick(titleImage, "选择标题栏图片")
            );
            Action(
                title,
                "清空图片",
                () =>
                {
                    titleImage.Text = "";
                    return Task.CompletedTask;
                }
            );
            var home = Card("主页");
            Choice(
                home,
                "主页模式",
                look,
                nameof(AppearanceSettings.HomeMode),
                [
                    "本地文件 / HTTPS",
                    "空白",
                    "内置预设"
                ],
                [
                    "document",
                    "blank",
                    "preset"
                ]
            );
            var presets = saved["Presets"]!.ToObject<Pcl.Core.Contracts.Workspaces.HomePresetView[]>()!;
            Choice(
                home,
                "主页预设",
                look,
                nameof(AppearanceSettings.HomePreset),
                new[]
                {
                    "欢迎",
                    "快捷操作",
                    "你知道吗？"
                }.Concat(
                    presets.Select(
                        x => x.Name
                    )
                )
                    .ToArray(),
                new[]
                {
                    "welcome",
                    "shortcuts",
                    "tips"
                }.Concat(
                    presets.Select(
                        x => x.Id
                    )
                )
                    .ToArray()
            );
            Action(
                home,
                "打开主页文件夹",
                () =>
                {
                    Open(paths.Root);
                    return Task.CompletedTask;
                }
            );
            Action(
                home,
                "查看教程",
                async () =>
                {
                    var guide = new WorkspaceWindow
                    {
                        Title = "自定义主页教程",
                        Width = 720,
                        Height = 500,
                        Content = new TextBlock
                        {
                            Margin = new Thickness(24),
                            TextWrapping = TextWrapping.Wrap,
                            Text = "点击生成教学文件，修改 title、body 与 links。链接 kind 可为 url、navigate、copy、message、launch、refresh 或 join_room；navigate 支持 downloads、instances、tasks、settings、resources、packs、network。保存为 UTF-8 JSON，在主页地址中选择文件或填写 HTTPS URL，保存后回到启动页。网络文档不会执行 shell 或加载程序集。"
                        }
                    };
                    await guide.ShowDialog(this);
                }
            );
            var document = Text(
                home,
                "本地 JSON / XAML 或 HTTPS 地址",
                look,
                nameof(AppearanceSettings.HomeDocument)
            );
            Action(
                home,
                "选择主页文件",
                () => Pick(document, "选择主页 JSON / XAML")
            );
            Action(
                home,
                "生成教学文件",
                async () =>
                {
                    var file = await api.CallAsync<string>("workspace.settings.example", null);
                    document.Text = file;
                    status.Text = "已生成：" + file + "。保存后使用主页刷新。";
                }
            );
            Action(
                home,
                "预览已保存主页",
                async () => await ShowClassicCustomHomeAsync()
            );
            Action(
                home,
                "将已保存主页另存为预设",
                async () =>
                {
                    var fresh = await api.CallAsync<System.Text.Json.JsonElement>("workspace.settings.savePreset", null);
                    ApplyAppearance(
                        JObject.Parse(
                            fresh.GetRawText()
                        )["Appearance"]!.ToObject<AppearanceSettings>()!
                    );
                    await NavigateClassicAsync("setup-ui");
                }
            );
            Action(
                home,
                "删除当前自定义预设",
                async () =>
                {
                    var fresh = await api.CallAsync<System.Text.Json.JsonElement>("workspace.settings.removePreset", null);
                    ApplyAppearance(
                        JObject.Parse(
                            fresh.GetRawText()
                        )["Appearance"]!.ToObject<AppearanceSettings>()!
                    );
                    await NavigateClassicAsync("setup-ui");
                }
            );
            Note(home, "支持 JSON 和常用 XAML 布局、文字、按钮及主页事件。未知控件会显示具体错误；第三方 WPF 程序和脚本不会执行。预设保存内容副本，可离线使用。");
            var hidden = Card("功能隐藏");
            hidden.IsVisible = !appearance.HiddenRoutes.Contains("hide-settings");
            var routes = ((string[]? )look[nameof(AppearanceSettings.HiddenRoutes)]?.ToObject<string[]>() ?? []).ToHashSet();
            foreach (var(key, label)in new[]
            {
                ("launch", "启动页"),
                ("settings", "设置"),
                ("setup-launch", "启动设置"),
                ("setup-ui", "个性化"),
                ("setup-system", "其他设置"),
                ("multiplayer", "联机入口"),
                ("version-management", "版本管理"),
                ("version-edit", "版本修改"),
                ("mod-update", "Mod 更新"),
                ("hide-settings", "功能隐藏设置"),
                ("download", "下载"),
                ("more", "更多"),
                ("setup-link", "联机设置"),
                ("help", "帮助"),
                ("about", "关于与鸣谢"),
                ("toolbox", "百宝箱"),
                ("feedback", "反馈"),
                ("vote", "新功能投票"),
                ("mod", "Mod"),
                ("shader", "光影包"),
                ("resourcepack", "资源包"),
                ("datapack", "数据包"),
                ("modpack", "整合包")
            }

            )
            {
                var check = new CheckBox
                {
                    Content = "隐藏 " + label,
                    IsChecked = routes.Contains(key),
                    Tag = "HiddenRoute:" + key
                };
                check.IsCheckedChanged += (_, _) =>
                {
                    if (check.IsChecked == true)
                    {
                        routes.Add(key);
                    }
                    else
                    {
                        routes.Remove(key);
                    }

                    look[nameof(AppearanceSettings.HiddenRoutes)] = JArray.FromObject(
                        routes.Order()
                    );
                };
                hidden.Children.Add(check);
            }

            Note(hidden, "隐藏后可使用右上角“恢复界面”或 Ctrl+Shift+H 恢复全部入口。账户界面只展示游戏名，不存储或显示微软邮箱。");
        }
        else if (page == "PageSetupSystem")
        {
            var download = Card("下载");
            Choice(
                download,
                "版本列表源",
                network,
                nameof(NetworkOptions.ManifestSource),
                [
                    "官方",
                    "官方失败后镜像",
                    "镜像优先"
                ],
                [
                    "official",
                    "fallback",
                    "mirror"
                ]
            );
            Text(
                download,
                "版本清单镜像完整 HTTPS URL",
                network,
                nameof(NetworkOptions.ManifestMirror)
            );
            Number(
                download,
                "最大并发文件数",
                network,
                nameof(NetworkOptions.ConcurrentFiles),
                1,
                32
            );
            Number(
                download,
                "速度限制 KiB/s（0 不限速）",
                network,
                nameof(NetworkOptions.SpeedKiB),
                0,
                1048576,
                128
            );
            Toggle(
                download,
                "优先使用已配置镜像",
                network,
                nameof(NetworkOptions.PreferMirror)
            );
            Text(
                download,
                "镜像 HTTPS 根地址",
                network,
                nameof(NetworkOptions.MirrorBase)
            );
            Text(
                download,
                "替换的官方主机",
                network,
                nameof(NetworkOptions.MirrorHost)
            );
            Text(
                download,
                "代理地址（不含密码）",
                network,
                nameof(NetworkOptions.Proxy)
            );
            Toggle(
                download,
                "大文件启用分段下载",
                network,
                nameof(NetworkOptions.SegmentedDownloads)
            );
            Number(
                download,
                "单文件分段数",
                network,
                nameof(NetworkOptions.SegmentWorkers),
                2,
                8
            );
            Note(download, "镜像仅替换指定主机且必须有官方哈希；版本与社区元数据使用各自来源策略；登录保持官方验证路径。并发与限速供新任务读取，代理需重开应用。");
            Action(
                download,
                "下载任务 / 网络检查 / 缓存",
                async () => await new TaskCenterWindow(
                    service,
                    RunAsync,
                    Open,
                    () => operation?.Cancel()
                ).ShowDialog(this)
            );
            var community = Card("社区资源");
            Choice(
                community,
                "来源策略",
                network,
                nameof(NetworkOptions.ResourceSource),
                [
                    "官方",
                    "官方失败后镜像",
                    "镜像优先"
                ],
                [
                    "official",
                    "fallback",
                    "mirror"
                ]
            );
            Text(
                community,
                "兼容 Modrinth v2 的 HTTPS API 根地址",
                network,
                nameof(NetworkOptions.ResourceApiMirror)
            );
            Text(
                community,
                "兼容 CurseForge v1 的 HTTPS API 根地址（不转发密钥）",
                network,
                nameof(NetworkOptions.CurseForgeApiMirror)
            );
            Choice(
                community,
                "文件名格式",
                network,
                nameof(NetworkOptions.FileNameStyle),
                [
                    "原始文件名",
                    "【名称】文件名",
                    "[名称] 文件名",
                    "名称-文件名",
                    "文件名-名称"
                ],
                [
                    "original",
                    "brackets",
                    "square",
                    "prefix",
                    "suffix"
                ]
            );
            Choice(
                community,
                "Mod 管理样式",
                network,
                nameof(NetworkOptions.ModNameStyle),
                [
                    "名称在前",
                    "文件名在前"
                ],
                [
                    "name",
                    "file"
                ]
            );
            Action(
                community,
                "打开多源搜索",
                async () => await NavigateClassicAsync("mod")
            );
            Note(community, "文件命名用于新安装和更新的受管理资源；已导入的用户文件不会被自动重命名。依赖身份和校验仍依据清单。");
            var assist = Card("辅助功能");
            Toggle(
                assist,
                "正式版更新提示",
                network,
                nameof(NetworkOptions.NotifyRelease)
            );
            Toggle(
                assist,
                "测试版更新提示",
                network,
                nameof(NetworkOptions.NotifySnapshot)
            );
            Toggle(
                assist,
                "首次启动自动设置为中文（保留已有语言）",
                look,
                nameof(AppearanceSettings.FirstLaunchChinese)
            );
            Action(
                assist,
                "检查 Minecraft 新版本",
                async () => await NavigateClassicAsync("vanilla")
            );
            var launcher = Card("启动器");
            Action(
                launcher,
                "导出设置",
                async () =>
                {
                    var file = await StorageProvider.SaveFilePickerAsync(
                        new FilePickerSaveOptions
                        {
                            Title = "导出设置",
                            SuggestedFileName = "PCL-Mac-settings.json"
                        }
                    );
                    if (file?.TryGetLocalPath()is { } target)
                    {
                        await api.ExportAsync(
                            "workspace.settings.export",
                            null,
                            new { },
                            target
                        );
                        status.Text = "已导出已保存设置；不含账户令牌、媒体文件或启动前程序。";
                    }
                }
            );
            Action(
                launcher,
                "导入设置",
                async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new FilePickerOpenOptions
                        {
                            Title = "导入设置"
                        }
                    );
                    if (files.FirstOrDefault()?.TryGetLocalPath()is { } file)
                    {
                        var fresh = await api.CallAsync<System.Text.Json.JsonElement>(
                            "workspace.settings.import",
                            null,
                            new
                            {
                                path = file
                            }
                        );
                        ApplyAppearance(
                            JObject.Parse(
                                fresh.GetRawText()
                            )["Appearance"]!.ToObject<AppearanceSettings>()!
                        );
                        classicPage.Content = await BuildWindowsSettingsAsync(page);
                    }
                }
            );
            Action(
                launcher,
                "打开缓存与诊断",
                async () => await Bootstrap.WorkspacePages.Cache(this, paths)
                    .ShowDialog(this)
            );
            Note(launcher, "当前构建没有签名更新源和公告服务，也不收集匿名统计。");
            var debug = Card("调试选项");
            Toggle(
                debug,
                "禁止从其他已校验下载文件复制",
                network,
                nameof(NetworkOptions.DisableLocalCopy)
            );
            Toggle(
                debug,
                "调试模式（运行与下载日志）",
                network,
                nameof(NetworkOptions.DebugMode)
            );
            Number(
                debug,
                "添加请求延迟（毫秒，0 关闭）",
                network,
                nameof(NetworkOptions.ArtificialDelayMs),
                0,
                5000,
                100
            );
            Action(
                debug,
                "打开日志目录",
                () =>
                {
                    Open(
                        Path.Combine(paths.Root, "logs")
                    );
                    return Task.CompletedTask;
                }
            );
            Action(
                debug,
                "任务与诊断",
                async () => await new TaskCenterWindow(
                    service,
                    RunAsync,
                    Open,
                    () => operation?.Cancel()
                ).ShowDialog(this)
            );
            Note(debug, "保留现有运行日志与诊断导出；原版动画调速和人为延迟测试未移植。");
        }
        else
        {
            var connection = Card("连接选项");
            Note(connection, "Mac 使用已实现的 EasyTier 无 TUN 房间流程。原版节点服务与邀请码协议不同，不将自定义节点写入无效的原版字段。");
            Action(
                connection,
                "创建 / 加入房间与配置节点",
                async () => await Bootstrap.WorkspacePages.Multiplayer(this, service)
                    .ShowDialog(this)
            );
            var advanced = Card("高级设置");
            Action(
                advanced,
                "网络检查",
                async () => await Bootstrap.WorkspacePages.Cache(this, paths)
                    .ShowDialog(this)
            );
            save.IsVisible = false;
            status.Text = "连接设置在房间窗口应用；状态由实际子进程报告。";
        }

        return new ScrollViewer
        {
            Content = panel
        };
    }

    // 主页异步加载使用版本号避免旧请求覆盖新的设置；失败时显示错误而不执行文档内容。
    private async Task RefreshClassicHomeAsync()
    {
        if (!WindowsLayout)
        {
            return;
        }

        var revision = ++classicHomeRevision;
        if (appearance.HomeMode == "blank" || (appearance.HomeMode == "document" && appearance.HomeDocument.Length == 0))
        {
            classicCustomHome.IsVisible = false;
            classicCustomHome.Content = null;
            return;
        }

        try
        {
            var document = await new Pcl.Client.WorkspaceApi(
                Bootstrap.WorkspacePages.Operations(this, service)
            ).CallAsync<HomeDocument>("workspace.settings.home", null);
            if (revision != classicHomeRevision)
            {
                return;
            }

            var content = new StackPanel
            {
                Spacing = 12
            };
            if (document.Markup is not null)
            {
                content.Children.Add(
                    HomeMarkupView.Build(
                        document.Markup,
                        link => ExecuteHomeAction(
                            link,
                            new Button()
                        )
                    )
                );
            }
            else
            {
                content.Children.Add(
                    new TextBlock
                    {
                        Text = document.Body,
                        TextWrapping = TextWrapping.Wrap
                    }
                );
                foreach (var link in document.Links)
                {
                    var button = new Button
                    {
                        Content = link.Text
                    };
                    button.Click += async (_, _) => await ExecuteHomeAction(link, button);
                    content.Children.Add(button);
                }
            }

            classicCustomHome.Content = ClassicCard(document.Title, content);
            await UiMotion.FadeIn(classicCustomHome);
            classicCustomHome.IsVisible = true;
        }
        catch (Exception ex)
        {
            if (revision == classicHomeRevision)
            {
                classicCustomHome.Content = ClassicCard(
                    "主页加载失败",
                    new TextBlock
                    {
                        Text = Pcl.Client.WorkspaceErrors.Describe(ex),
                        TextWrapping = TextWrapping.Wrap
                    }
                );
                classicCustomHome.IsVisible = true;
            }
        }
    }

    // 默认主页保留版本和启动状态；自定义文档独立预览，链接继续通过原有验证服务处理。
    private Task ShowClassicCustomHomeAsync()
    {
        CustomHomeClick(
            null,
            new Avalonia.Interactivity.RoutedEventArgs()
        );
        return Task.CompletedTask;
    }
}
