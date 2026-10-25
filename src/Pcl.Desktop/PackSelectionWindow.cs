using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 整合包兼容：检查清单及归档路径，转换受支持组件，下载校验内容并安装到隔离实例。
public sealed class PackSelectionWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly Dictionary<string, CheckBox> choices = new(StringComparer.Ordinal);
    public string[] SelectedFiles => choices.Where(
        x => x.Value.IsChecked == true
    )
        .Select(
            x => x.Key
        )
        .ToArray();

    public PackSelectionWindow(PortablePackPlan plan, bool includeAll = false)
    {
        Title = "选择整合包文件";
        Width = 650;
        Height = 540;
        MinWidth = 500;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(24),
            LastChildFill = true
        };
        var heading = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        heading.Children.Add(
            new TextBlock
            {
                Text = plan.Name,
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        );
        heading.Children.Add(
            new TextBlock
            {
                Text = $"Minecraft {plan.Minecraft} · {plan.Loader}\n必需文件自动安装；勾选要安装的可选文件。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var bulk = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var all = new Button
        {
            Content = "全选"
        };
        var none = new Button
        {
            Content = "全不选"
        };
        bulk.Children.Add(all);
        bulk.Children.Add(none);
        heading.Children.Add(bulk);
        all.Click += (_, _) =>
        {
            foreach (var box in choices.Values)
                box.IsChecked = true;
        };
        none.Click += (_, _) =>
        {
            foreach (var box in choices.Values)
                box.IsChecked = false;
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消"
        };
        var install = new Button
        {
            Content = "按此选择安装",
            Name = "ConfirmOptionalFiles"
        };
        footer.Children.Add(cancel);
        footer.Children.Add(install);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var list = new StackPanel
        {
            Spacing = 8
        };
        foreach (var item in plan.OptionalFiles)
        {
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = item.Name + (item.Size is long size
                        ? $"  ({size / 1048576.0:F2} MiB)"
                        : ""),
                    TextWrapping = TextWrapping.Wrap
                },
                Tag = item.Id,
                IsChecked = includeAll
            };
            choices[item.Id] = box;
            list.Children.Add(box);
        }

        if (choices.Count == 0)
            list.Children.Add(
                new TextBlock
                {
                    Text = "此整合包没有声明可选文件。"
                }
            );
        root.Children.Add(
            new ScrollViewer
            {
                Content = list
            }
        );
        Content = root;
        cancel.Click += (_, _) => Close();
        install.Click += (_, _) => Close(SelectedFiles);
    }

    // 可选清单来自 Host 的已校验预览；选择后由 Host 重新核对归档指纹。
    public static async Task<string[]> SelectAsync(Window owner, PortablePackPlan plan, bool includeAll, CancellationToken ct)
    {
        if (plan.OptionalFiles.Count == 0)
        {
            return[];
        }

        var window = new PackSelectionWindow(plan, includeAll);
        using var cancellation = ct.Register(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => window.Close()
            )
        );
        var selected = await window.ShowDialog<string[]?>(owner);
        ct.ThrowIfCancellationRequested();
        return selected ?? throw new OperationCanceledException("已取消整合包选择。");
    }
}
