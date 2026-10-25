using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
/// <summary>只显示 Host 算出的移除/保留/冲突集合；确认后仍由 Host 重新校验实际文件和依赖。</summary>
public sealed class IrisEnvironmentWindow : WorkspaceWindow
{
    internal Button Confirm { get; } = new()
    {
        Content = "确认卸载 Iris 环境"
    };
    internal Button Cancel { get; } = new()
    {
        Content = "取消"
    };

    public IrisEnvironmentWindow(ShaderEnvironmentPreview plan)
    {
        Title = "卸载 Iris 光影环境";
        Width = 760;
        Height = 690;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "卸载 Iris 光影环境",
                FontSize = 24,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "仅移除有可信安装记录且未被修改的独占组件。共享依赖、手动安装文件、光影包、配置和存档保留。游戏运行时不执行。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        foreach (var(title, rows)in new[]
        {
            ("将移除", plan.Remove),
            ("将保留", plan.Retain),
            ("冲突", plan.Conflicts)
        }

        )
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.SemiBold
                }
            );
            panel.Children.Add(
                new TextBlock
                {
                    Text = rows.Length == 0
                        ? "无"
                        : string.Join("\n", rows),
                    TextWrapping = TextWrapping.Wrap
                }
            );
        }

        Confirm.IsEnabled = plan.CanApply;
        var actions = new StackPanel
        {
            Spacing = 10
        };
        actions.Children.Add(Confirm);
        actions.Children.Add(Cancel);
        panel.Children.Add(actions);
        Content = new ScrollViewer
        {
            Content = panel
        };
        Confirm.Click += (_, _) => Close(true);
        Cancel.Click += (_, _) => Close(false);
    }
}
