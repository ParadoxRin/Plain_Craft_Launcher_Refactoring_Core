using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 本地视觉行为独立管理：定时器、位图与隐藏恢复都有明确生命周期，不只是配置存储。
public partial class MainWindow
{
    private readonly Border classicBackground = new()
    {
        IsHitTestVisible = false
    };
    private readonly Border startupLogo = new()
    {
        IsVisible = false,
        Background = Brushes.White,
        CornerRadius = new CornerRadius(20),
        Width = 160,
        Height = 160,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M26,29 v-25 h6 a7,7 180 0 1 0,14 h-6 M62.5,6.5 a10,11.5 180 1 0 0,18 M71,2.5 v24.5 h13.5"),
            Stroke = Brush.Parse("#0875D1"),
            StrokeThickness = 2,
            Stretch = Stretch.Uniform,
            Width = 120,
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
    private readonly Border classicTint = new()
    {
        IsHitTestVisible = false
    };
    private readonly Image classicTitleImage = new()
    {
        Width = 150,
        Height = 36,
        Stretch = Stretch.Uniform,
        IsVisible = false
    };
    private readonly Button classicRecovery = new()
    {
        Content = "恢复界面",
        IsVisible = false,
        FontSize = 11
    };
    private readonly DispatcherTimer accentTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100)
    };
    private Bitmap? titleBitmap;
    private double hue;
    internal AppearanceSettings CurrentAppearance => appearance;

    // [实现] InitializeLocalAppearance 的实现：执行 SizeChanged += (_, _) => RefreshAutomaticBackground()；执行 accentTimer.Tick += (_, _) => { hue = (hue + .006 * appearance.AnimationSpeed) % 1; double offs…；执行 classicRecovery.Click += async (_, _) => { var restored = appearance with { HiddenRoutes = [], …。
    private void InitializeLocalAppearance()
    {
        SizeChanged += (_, _) => RefreshAutomaticBackground();
        accentTimer.Tick += (_, _) =>
        {
            hue = (hue + .006 * appearance.AnimationSpeed) % 1;
            ApplyAnimatedPalette();
        };
        classicRecovery.Click += async (_, _) =>
        {
            // [实现] 回调触发时：准备 var restored = appearance with { HiddenRoutes = [], HiddenNavigation = [] }；执行 new AppearanceService(paths).Save(restored)；执行 ApplyAppearance(restored)。
            var restored = appearance with
            {
                HiddenRoutes = [],
                HiddenNavigation = []
            };
            await new Pcl.Client.WorkspaceApi(
                Bootstrap.WorkspacePages.Operations(this, service)
            ).CallAsync<System.Text.Json.JsonElement>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = restored
                    }
                }
            );
            ApplyAppearance(restored);
            await NavigateClassicAsync("setup-ui");
        };
        KeyDown += (_, e) =>
        {
            // [实现] 回调触发时：按 e.Key == Avalonia.Input.Key.H && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) &&… 分支处理。
            if (e.Key == Avalonia.Input.Key.H && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            {
                // [实现] 条件成立（e.Key == Avalonia.Input.Key.H && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) &&…）时：执行 classicRecovery.RaiseEvent( new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent) )。
                classicRecovery.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)
                );
            }
        };
        Opened += async (_, _) =>
        {
            // [实现] 回调触发时：按 appearance.ShowStartupLogo && appearance.AnimationSpeed > 0 分支处理。
            if (appearance.ShowStartupLogo && appearance.AnimationSpeed > 0)
            {
                // [实现] 条件成立（appearance.ShowStartupLogo && appearance.AnimationSpeed > 0）时：执行 startupLogo.IsVisible = true；执行 await Task.Delay((int)(900 / appearance.AnimationSpeed))；执行 startupLogo.IsVisible = false。
                startupLogo.IsVisible = true;
                await UiMotion.FadeIn(startupLogo, 600);
                await Task.Delay(
                    (int)(300 / Math.Max(.1, appearance.AnimationSpeed))
                );
                startupLogo.IsVisible = false;
            }
        };
    }

    // [实现] ApplyLocalAppearance 的实现：按 classicHeader is null 分支处理；执行 accentTimer.Stop()；按 value.AccentMode != "static" && value.AnimationSpeed > 0 分支处理。
    private void ApplyLocalAppearance(AppearanceSettings value)
    {
        if (classicHeader is null)
        {
            // [实现] 条件成立（classicHeader is null）时：结束当前过程。
            return;
        }

        UiMotion.Configure(value.AnimationSpeed);
        accentTimer.Stop();
        if (value.AccentMode != "static" && value.AnimationSpeed > 0)
        {
            // [实现] 条件成立（value.AccentMode != "static" && value.AnimationSpeed > 0）时：执行 accentTimer.Start()。
            accentTimer.Start();
        }

        if (value.TitleImage.Length > 0 && new FileInfo(value.TitleImage).Length > 30_000_000)
        {
            // [实现] 条件成立（value.TitleImage.Length > 0 && new FileInfo(value.TitleImage).Length > 30_000_000）时：抛出 new InvalidDataException("标题栏图片不能超过 30 MB。")。
            throw new InvalidDataException("标题栏图片不能超过 30 MB。");
        }

        Bitmap? next = value.TitleImage.Length > 0
            ? new Bitmap(value.TitleImage)
            : null;
        classicTitleImage.Source = next;
        titleBitmap?.Dispose();
        titleBitmap = next;
        classicTitleImage.IsVisible = value.TitleMode == "image" && next is not null;
        if (classicBrand is not null)
        {
            // [实现] 条件成立（classicBrand is not null）时：执行 classicBrand.IsVisible = value.TitleMode != "image"。
            classicBrand.IsVisible = true;
        }

        BackgroundImage.IsVisible = false;
        if (BackgroundImage.Source is Bitmap image)
        {
            // [实现] 条件成立（BackgroundImage.Source is Bitmap image）时：准备 var position = value.BackgroundFit；按 position == "auto" 分支处理；执行 classicBackground.Background = new ImageBrush(image) { Stretch = position == "stretch" ? Stretc…。
            var position = value.BackgroundFit;
            // 与原版 FormMain 的自动策略一致：两边均小于窗口一半时平铺，否则保持比例填满。
            if (position == "auto")
            {
                // [实现] 条件成立（position == "auto"）时：执行 position = image.Size.Width < Bounds.Width / 2 && image.Size.Height < Bounds.Height / 2 ? "tile…。
                position = image.Size.Width < Bounds.Width / 2 && image.Size.Height < Bounds.Height / 2
                    ? "tile"
                    : "fill";
            }

            classicBackground.Background = new ImageBrush(image)
            {
                Stretch = position == "stretch"
                    ? Stretch.Fill
                    : position is "tile" or "center"
                        ? Stretch.None
                        : position == "fit"
                            ? Stretch.Uniform
                            : Stretch.UniformToFill,
                AlignmentX = position.Contains("left") || position == "left"
                    ? AlignmentX.Left
                    : position.Contains("right") || position == "right"
                        ? AlignmentX.Right
                        : AlignmentX.Center,
                AlignmentY = position.Contains("top") || position == "top"
                    ? AlignmentY.Top
                    : position.Contains("bottom") || position == "bottom"
                        ? AlignmentY.Bottom
                        : AlignmentY.Center,
                TileMode = position == "tile"
                    ? TileMode.Tile
                    : TileMode.None,
                DestinationRect = position == "tile"
                    ? new RelativeRect(
                        new Rect(0, 0, image.Size.Width, image.Size.Height),
                        RelativeUnit.Absolute
                    )
                    : RelativeRect.Fill
            };
        }
        else
        {
            // [实现] 前一条件不成立时：执行 classicBackground.Background = null。
            classicBackground.Background = null;
        }

        classicBackground.Opacity = value.BackgroundOpacity;
        classicBackground.Effect = BackgroundImage.Effect;
        classicTint.Background = Brush.Parse(value.Accent);
        classicTint.Opacity = value.BackgroundTint
            ? .18
            : 0;
        classicRecovery.IsVisible = value.HiddenRoutes.Length > 0;
        VersionButton.IsVisible = !value.HiddenRoutes.Contains("version-management");
        FabricButton.IsVisible = !value.HiddenRoutes.Contains("version-edit");
        ApplyHiddenActions(this, value);
    }

    // 两种动态色方案共用主色资源和三段渐变；辅助窗也继承主色，颜色变化不依赖任何解锁流程。
    private void ApplyAnimatedPalette()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var angle = appearance.AccentMode == "aurora"
            ? 155 + 45 * Math.Sin(hue * Math.PI * 2)
            : hue * 360;
        Color Shade(double delta, double light) => new HslColor(1, (angle + delta + 360) % 360, .82, light).ToRgb();
        var accent = new SolidColorBrush(
            Shade(0, .43)
        );
        Resources["PclAccent"] = accent;
        Resources["PclSelection"] = new SolidColorBrush(
            Shade(0, .93)
        );
        Resources["PclBackground"] = new SolidColorBrush(
            Shade(15, .94)
        );
        if (classicHeader is not null)
        {
            classicHeader.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(
                        Shade(-20, .46),
                        0
                    ),
                    new GradientStop(
                        Shade(0, .53),
                        .5
                    ),
                    new GradientStop(
                        Shade(20, .46),
                        1
                    )
                }
            };
        }
    }

    // 首次布局和窗口缩放后重新计算自动平铺，避免构造窗口时 Bounds 为零导致重开不生效。
    private void RefreshAutomaticBackground()
    {
        if (appearance.BackgroundFit != "auto" || BackgroundImage.Source is not Bitmap image || classicBackground.Background is not ImageBrush brush)
        {
            // [实现] 条件成立（appearance.BackgroundFit != "auto" || BackgroundImage.Source is not Bitmap image || classicBack…）时：结束当前过程。
            return;
        }

        var tile = image.Size.Width < Bounds.Width / 2 && image.Size.Height < Bounds.Height / 2;
        brush.TileMode = tile
            ? TileMode.Tile
            : TileMode.None;
        brush.Stretch = tile
            ? Stretch.None
            : Stretch.UniformToFill;
        brush.DestinationRect = tile
            ? new RelativeRect(
                new Rect(0, 0, image.Size.Width, image.Size.Height),
                RelativeUnit.Absolute
            )
            : RelativeRect.Fill;
    }

    // 不改变已授权功能的能力；这里只隐藏用户指定的 UI 操作，恢复按钮永远保留。
    internal static void ApplyHiddenActions(Control root, AppearanceSettings value) => ApplyHiddenActions(root, value.HiddenRoutes);
    internal static void ApplyHiddenActions(Control root, string[] hiddenRoutes)
    {
        foreach (var button in root.GetVisualDescendants()
            .OfType<Button>())
        {
            // [实现] 对 root.GetVisualDescendants() .OfType<Button>() 中的每一项：准备 var text = button.Content as string；按 text is "版本选择" or "实例管理" 分支处理；按 text is "版本设置" or "调整加载器" or "修改版本" 分支处理。
            var text = button.Content as string;
            if (text is "版本选择" or "实例管理")
            {
                // [实现] 条件成立（text is "版本选择" or "实例管理"）时：执行 button.IsVisible = !hiddenRoutes.Contains("version-management")。
                button.IsVisible = !hiddenRoutes.Contains("version-management");
            }

            if (text is "版本设置" or "调整加载器" or "修改版本")
            {
                // [实现] 条件成立（text is "版本设置" or "调整加载器" or "修改版本"）时：执行 button.IsVisible = !hiddenRoutes.Contains("version-edit")。
                button.IsVisible = !hiddenRoutes.Contains("version-edit");
            }

            if (text is "更新" or "检查更新" or "批量更新" or "安装所选更新" or "批量更新所选" or "提交整批更新" or "忽略此版本")
            {
                // [实现] 条件成立（text is "更新" or "检查更新" or "批量更新" or "安装所选更新" or "批量更新所选" or "提交整批更新"）时：执行 button.IsVisible = !hiddenRoutes.Contains("mod-update")。
                button.IsVisible = !hiddenRoutes.Contains("mod-update");
            }

            if (text is "联机" or "创建 / 加入房间与配置节点")
            {
                // [实现] 条件成立（text is "联机" or "创建 / 加入房间与配置节点"）时：执行 button.IsVisible = !hiddenRoutes.Contains("multiplayer")。
                button.IsVisible = !hiddenRoutes.Contains("multiplayer");
            }
        }
    }
}
