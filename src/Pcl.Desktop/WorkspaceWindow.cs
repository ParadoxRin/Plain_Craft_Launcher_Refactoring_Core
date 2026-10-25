using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;

namespace Pcl.Desktop;
/// <summary>辅助页面统一使用返回标题栏、左侧分类和白色工作区；复用原控件与事件，保留取消/关闭生命周期。</summary>
public class WorkspaceWindow : Window
{
    // [实现] WorkspaceWindow 构造 的实现：执行 Opened += (_, _) => ApplyWorkspaceStructure()。
    public WorkspaceWindow()
    {
        Opened += async (_, _) =>
        {
            ApplyWorkspaceStructure();
            if (Content is Control content)
            {
                await UiMotion.FadeIn(content);
            }
        };
    }

    internal bool WorkspaceStructured { get; private set; }

    // [实现] ApplyWorkspaceStructure 的实现：按 WorkspaceStructured || Program.Arguments.Contains("--modern-layout") || Content is not Control … 分支处理；执行 WorkspaceStructured = true；执行 RequestedThemeVariant = ThemeVariant.Light。
    private void ApplyWorkspaceStructure()
    {
        if (WorkspaceStructured || Program.Arguments.Contains("--modern-layout") || Content is not Control body)
        {
            // [实现] 条件成立（WorkspaceStructured || Program.Arguments.Contains("--modern-layout") || Content is not Control …）时：结束当前过程。
            return;
        }

        WorkspaceStructured = true;
        RequestedThemeVariant = ThemeVariant.Light;
        var tabStyle = new Style(
            x => x.OfType<TabItem>()
        );
        tabStyle.Setters.Add(
            new Setter(TabItem.FontSizeProperty, 14d)
        );
        tabStyle.Setters.Add(
            new Setter(
                TabItem.PaddingProperty,
                new Thickness(12, 10)
            )
        );
        Styles.Add(tabStyle);
        Background = Brush.Parse("#DCEAF9");
        Foreground = Brush.Parse("#303B45");
        Content = null;
        var header = new DockPanel
        {
            Background = Brush.Parse("#126CC5"),
            Height = 48
        };
        if (Owner is MainWindow paletteOwner)
        {
            header.Bind(
                Panel.BackgroundProperty,
                paletteOwner.GetResourceObservable("PclAccent")
            );
        }

        var back = new Button
        {
            Content = "←",
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Margin = new Thickness(10, 5)
        };
        back.Click += (_, _) => Close();
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        header.Children.Add(
            new TextBlock
            {
                Text = Title,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold
            }
        );
        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(
            new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(16),
                Child = body
            }
        );
        Content = layout;
        // 将原本顶部的标签页变为 Windows 辅助页的左侧目录，不复制业务控件。
        if (body is TabControl rootTabs)
        {
            // [实现] 条件成立（body is TabControl rootTabs）时：执行 rootTabs.TabStripPlacement = Dock.Left。
            rootTabs.TabStripPlacement = Dock.Left;
        }

        foreach (var tabs in body.GetVisualDescendants()
            .OfType<TabControl>())
        {
            // [实现] 对 body.GetVisualDescendants() .OfType<TabControl>() 中的每一项：执行 tabs.TabStripPlacement = Dock.Left。
            tabs.TabStripPlacement = Dock.Left;
        }

        foreach (var tabs in body.GetLogicalDescendants()
            .OfType<TabControl>())
        {
            // [实现] 对 body.GetLogicalDescendants() .OfType<TabControl>() 中的每一项：执行 tabs.TabStripPlacement = Dock.Left。
            tabs.TabStripPlacement = Dock.Left;
        }

        if (Owner is MainWindow main)
        {
            // [实现] 条件成立（Owner is MainWindow main）时：执行 MainWindow.ApplyHiddenActions(this, main.CurrentAppearance)。
            MainWindow.ApplyHiddenActions(this, main.CurrentAppearance);
        }

        MinWidth = Math.Max(MinWidth, 640);
        Width = Math.Max(Width, 760);
    }
}
