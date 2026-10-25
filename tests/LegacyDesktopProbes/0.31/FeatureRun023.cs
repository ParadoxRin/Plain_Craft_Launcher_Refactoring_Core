using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 本轮新增功能的独立运行脚本；不调用历史回归套件，不读取真实账户或存档。
public partial class MainWindow
{
    private async Task FeatureRun023Async()
    {
        var observations = new List<object>();
        void Record(string feature, object result)
        {
            observations.Add(
                new
                {
                    feature,
                    result
                }
            );
            JsonFiles.Write(
                Path.Combine(
                    paths.Evidence,
                    Program.Arguments.Contains("--feature-resources-023")
                        ? "resource-run-0.23.json"
                        : "feature-run-0.23.json"
                ),
                observations
            );
            Console.WriteLine("FEATURE_023 " + feature);
        }

        try
        {
            RuntimeTrace.Configure(paths, true);
            if (Program.Arguments.Contains("--feature-fragment-023"))
            {
                var fragmentMarkup = "<local:MyCard Title=\"片段主页\"><TextBlock Text=\"真实控件\" Foreground=\"{DynamicResource ColorBrush1}\"/><local:MyIconButton EventType=\"刷新主页\" EventData=\"/\" Logo=\"M0,0 L10,10 L0,10 Z\"/></local:MyCard>";
                var file = Path.Combine(paths.Root, "fragment-home.xaml");
                File.WriteAllText(file, fragmentMarkup);
                var fragmentValue = appearance with
                {
                    HomeMode = "document",
                    HomeDocument = file,
                    AccentMode = "rainbow",
                    AnimationSpeed = 1
                };
                new AppearanceService(paths).Save(fragmentValue);
                ApplyAppearance(fragmentValue);
                await RefreshClassicHomeAsync();
                await ExecuteHomeAction(
                    new HomeLink("刷新", "/", "refresh"),
                    new Button()
                );
                var view = HomeMarkupView.Build(
                    fragmentMarkup,
                    link => ExecuteHomeAction(
                        link,
                        new Button()
                    )
                );
                var fragmentWindow = new WorkspaceWindow
                {
                    Title = "原版片段主页运行",
                    Content = view
                };
                fragmentWindow.Show(this);
                await Task.Delay(150);
                fragmentWindow.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => x.Content is Avalonia.Controls.Shapes.Path
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                await Task.Delay(250);
                JsonFiles.Write(
                    Path.Combine(paths.Evidence, "fragment-run-0.23.json"),
                    new
                    {
                        elements = fragmentWindow.GetVisualDescendants()
                            .Count(),
                        inlineHomeVisible = classicCustomHome.IsVisible,
                        inlineHomeType = classicCustomHome.Content?.GetType()
                            .Name,
                        structured = fragmentWindow.WorkspaceStructured
                    }
                );
                fragmentWindow.Close();
                Close();
                return;
            }

            if (Program.Arguments.Contains("--feature-resources-023"))
            {
                await ResourceFeatureRun023Async(Record);
                Record("本轮完成", DateTimeOffset.UtcNow);
                Close();
                return;
            }

            var manager = new AccountManager(
                paths,
                new MacAccountSecrets("pcl023-feature-run")
            );
            var player = manager.AddOffline("PCL023_Skin");
            var offline = new OfflineSkinService(paths);
            var window = Bootstrap.WorkspacePages.Skins(this, manager);
            window.Show(this);
            await Task.Delay(250);
            async Task Click(string label)
            {
                var button = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => Equals(x.Content, label)
                    );
                button.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                var limit = DateTime.UtcNow.AddSeconds(100);
                while (window.IsWorking023 && DateTime.UtcNow < limit)
                {
                    await Task.Delay(100);
                }

                Record(label, window.Status023);
                if (window.IsWorking023)
                {
                    throw new TimeoutException(label);
                }
            }

            foreach (var label in new[]
            {
                "Steve 纹理",
                "Alex 纹理",
                "随机默认皮肤"
            }

            )
            {
                await Click(label);
                Record(
                    label + "持久化",
                    offline.Get(player)!
                );
                var session = AccountManager.OfflineSession(
                    player.Name,
                    offline.Get(player)?.Model ?? "auto"
                );
                Record(
                    label + "启动身份",
                    new
                    {
                        session.Uuid,
                        model = offline.Get(player)?.Model
                    }
                );
            }

            window.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "PublicSkinPlayerName"
                )
                .Text = "Notch";
            await Click("获取正版玩家皮肤");
            await Click("保存获取的皮肤");
            await Click("刷新正版皮肤");
            var saved = offline.Get(player) ?? throw new IOException("未保存皮肤");
            Record("玩家名持久化", saved);
            var image = offline.ImagePath(player)!;
            offline.Export(
                player,
                Path.Combine(paths.Evidence, "exported-player-skin.png")
            );
            using (var png = new Bitmap(image))
            {
                Record(
                    "皮肤解码",
                    new
                    {
                        png.PixelSize.Width,
                        png.PixelSize.Height
                    }
                );
            }

            using (var png = new Bitmap(
                new MemoryStream(
                    SkinPixels.Legacy(
                        File.ReadAllBytes(image)
                    )
                )
            ))
            {
                Record(
                    "旧版皮肤转换",
                    new
                    {
                        png.PixelSize.Width,
                        png.PixelSize.Height
                    }
                );
            }

            window.Close();
            var reopened = Bootstrap.WorkspacePages.Skins(
                this,
                new AccountManager(
                    paths,
                    new MacAccountSecrets("pcl023-feature-run")
                )
            );
            reopened.Show(this);
            await Task.Delay(150);
            Record(
                "重开皮肤窗口",
                reopened.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(
                        x => x.Name == "PublicSkinPlayerName"
                    )
                    .Text ?? ""
            );
            reopened.Close();
            foreach (var mode in new[]
            {
                "rainbow",
                "aurora"
            }

            )
            {
                ApplyAppearance(
                    appearance with
                    {
                        AccentMode = mode,
                        AnimationSpeed = 2
                    }
                );
                await Task.Delay(200);
                var first = Resources["PclAccent"]!.ToString();
                await Task.Delay(300);
                Record(
                    mode,
                    new
                    {
                        first,
                        second = Resources["PclAccent"]!.ToString(),
                        header = classicHeader?.Background?.GetType()
                            .Name
                    }
                );
            }

            foreach (var speed in new[]
            {
                0d,
                .5,
                3d
            }

            )
            {
                UiMotion.Configure(speed);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await UiMotion.FadeIn(classicPage);
                Record(
                    "动画速度 " + speed,
                    new
                    {
                        elapsedMs = clock.ElapsedMilliseconds,
                        classicPage.Opacity
                    }
                );
            }

            startupLogo.IsVisible = true;
            await UiMotion.FadeIn(startupLogo, 300);
            Record(
                "启动标志",
                startupLogo.Child?.GetType()
                    .FullName ?? ""
            );
            startupLogo.IsVisible = false;
            const string markup = """
                <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:PCL">
                  <local:MyCard Title="PCL 0.23 本地主页" Padding="16">
                    <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                      <TextBlock Text="主页布局与复制操作" Grid.Column="0"/>
                      <local:MyButton Text="复制" Grid.Column="1" EventType="复制文本" EventData="PCL023-home"/>
                    </Grid>
                  </local:MyCard>
                </StackPanel>
                """;
            var documentFile = Path.Combine(paths.Root, "feature-home.xaml");
            File.WriteAllText(documentFile, markup);
            var value = appearance with
            {
                HomeMode = "document",
                HomeDocument = documentFile,
                AccentMode = "static"
            };
            new AppearanceService(paths).Save(value);
            ApplyAppearance(value);
            await RefreshClassicHomeAsync();
            var home = await new AppearanceService(paths).ReadHomeAsync();
            var homeView = HomeMarkupView.Build(
                home.Markup!,
                link => ExecuteHomeAction(
                    link,
                    new Button()
                )
            );
            var homeWindow = new WorkspaceWindow
            {
                Title = "本轮主页运行",
                Content = homeView
            };
            homeWindow.Show(this);
            await Task.Delay(150);
            homeWindow.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => Equals(x.Content, "复制")
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Task.Delay(150);
            Record(
                "XAML 布局与事件",
                new
                {
                    elements = homeWindow.GetVisualDescendants()
                        .Count(),
                    markup = home.Markup!.Length
                }
            );
            homeWindow.Close();
            var preset = new HomePresets(paths).Save("实际运行预设", home);
            new AppearanceService(paths).Save(
                value with
                {
                    HomeMode = "preset",
                    HomePreset = preset.Id
                }
            );
            var loaded = await new AppearanceService(paths).ReadHomeAsync();
            Record(
                "预设保存重读",
                new
                {
                    preset.Name,
                    sameContent = loaded.Markup == markup
                }
            );
            new HomePresets(paths).Remove(preset.Id);
            Record(
                "预设删除",
                new HomePresets(paths).List()
                    .Count
            );
            new AppearanceService(paths).Save(value);
            await ResourceFeatureRun023Async(Record);
            Record("本轮完成", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Record(
                "本轮失败",
                new
                {
                    type = ex.GetType()
                        .Name,
                    message = Pcl.Client.WorkspaceErrors.Describe(ex)
                }
            );
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }

        Close();
    }

    private async Task ResourceFeatureRun023Async(Action<string, object> Record)
    {
        var manager = new AccountManager(
            paths,
            new MacAccountSecrets("pcl023-feature-run")
        );
        var offline = new OfflineSkinService(paths);
        var player = manager.Accounts.First(
            x => x.Kind == AccountKind.Offline
        );
        offline.Export(
            player,
            Path.Combine(paths.Evidence, "skin-export-service.png")
        );
        Record(
            "皮肤导出服务",
            new FileInfo(
                Path.Combine(paths.Evidence, "skin-export-service.png")
            ).Length
        );
        var options = new NetworkOptions
        {
            DebugMode = true,
            ResourceSource = "mirror",
            ResourceApiMirror = "https://api.modrinth.com/v2/pcl023-missing"
        };
        DownloadTasks.For(paths)
            .Configure(options);
        using var catalog = new ResourceCatalog(paths: paths);
        var releases = await catalog.VersionsAsync("sodium", ResourceKind.Mod, "1.21.1", "fabric");
        var release = releases.First();
        Record(
            "来源失败回退与项目名",
            new
            {
                release.Id,
                release.ProjectTitle,
                release.Name
            }
        );
        var byHash = await catalog.VersionFromHashAsync(release.File.Sha1);
        Record(
            "哈希查询来源策略",
            new
            {
                byHash?.Id,
                byHash?.ProjectTitle
            }
        );
        using var downloads = new DownloadService(paths: paths);
        foreach (var style in new[]
        {
            "original",
            "brackets",
            "square",
            "prefix",
            "suffix"
        }

        )
        {
            DownloadTasks.For(paths)
                .Configure(
                    options with
                    {
                        FileNameStyle = style
                    }
                );
            var profile = Directory.EnumerateFiles(
                Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "runtime/apple-silicon-1.21.1/instances"
                ),
                "profile.json",
                SearchOption.AllDirectories
            )
                .First(
                    x => (string? )JsonFiles.Read(x)["loader"] == "fabric" && (string? )JsonFiles.Read(x)["gameVersion"] == "1.21.1"
                );
            Directory.CreateDirectory(
                Path.Combine(paths.Root, "resource-" + style)
            );
            File.Copy(
                profile,
                Path.Combine(paths.Root, "resource-" + style, "profile.json"),
                true
            );
            var installer = new ResourceInstaller(
                Path.Combine(paths.Root, "resource-" + style),
                downloads,
                "fabric",
                "1.21.1"
            );
            var installed = await installer.InstallAsync(
                new(
                    ResourceKind.Mod,
                    release.ProjectId,
                    [
                        release
                    ]
                )
            );
            Record(
                "命名下载安装 " + style,
                installed.Select(
                    x => new
                    {
                        x.Name,
                        x.FileName
                    }
                )
                    .ToArray()
            );
            Record(
                "管理名称显示 " + style,
                installed.Select(
                    x => new
                    {
                        nameFirst = LocalResourcePreferences.Display(x, "name"),
                        fileFirst = LocalResourcePreferences.Display(x, "file")
                    }
                )
                    .ToArray()
            );
        }

        RuntimeTrace.Write("feature.finished");
        Record(
            "运行调试日志",
            new FileInfo(
                Path.Combine(paths.Root, "logs", "runtime-debug.jsonl")
            ).Length
        );
        RuntimeTrace.Configure(paths, false);
    }
}
