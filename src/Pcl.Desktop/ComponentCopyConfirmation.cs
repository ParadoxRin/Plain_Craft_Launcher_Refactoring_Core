using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Core.Contracts.Pages;

namespace Pcl.Desktop;
/// <summary>创建副本前显示 Host 已确定的路径；取消不提交计划或保存提示偏好。</summary>
public sealed class ComponentCopyConfirmation : WorkspaceWindow
{
    internal Button ConfirmButton { get; } = new()
    {
        Content = "确认创建副本"
    };
    internal Button CancelButton { get; } = new()
    {
        Content = "取消"
    };
    internal CheckBox Suppress { get; } = new()
    {
        Content = "以后不再显示此说明（仍会校验冲突和运行状态）"
    };
    internal TextBlock Feedback { get; } = new()
    {
        TextWrapping = TextWrapping.Wrap
    };

    public ComponentCopyConfirmation(ComponentPreview plan)
    {
        Title = "确认创建加载器副本";
        Width = 730;
        Height = 700;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "创建新的游戏实例",
                FontSize = 23,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "复制游戏数据与存档，重建目标组件；资源库与原实例共享，自定义 JVM 和游戏参数重置。原实例保留。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        AddPath(panel, "原实例", plan.SourceDirectory, plan.SourceDirectory);
        AddPath(
            panel,
            "新副本（创建后才存在）",
            plan.TargetDirectory,
            Path.GetDirectoryName(plan.TargetDirectory)!
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "取消不会创建目标实例、修改源实例或开始安装。预览记录与实例容器目录会保留。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(Suppress);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        actions.Children.Add(ConfirmButton);
        actions.Children.Add(CancelButton);
        panel.Children.Add(actions);
        panel.Children.Add(Feedback);
        Content = new ScrollViewer
        {
            Content = panel
        };
        ConfirmButton.Click += (_, _) => Close(
            new CopyConfirmation(true, Suppress.IsChecked == true)
        );
        CancelButton.Click += (_, _) => Close(
            new CopyConfirmation(false, false)
        );
    }

    // 路径由 Host 给出；创建前打开目标父目录，绝不为“打开”而创建目标目录。
    private void AddPath(StackPanel panel, string title, string path, string folder)
    {
        panel.Children.Add(
            new TextBlock
            {
                Text = title
            }
        );
        panel.Children.Add(
            new TextBox
            {
                Text = path,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap
            }
        );
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var copy = new Button
        {
            Content = "复制路径"
        };
        var open = new Button
        {
            Content = folder == path
                ? "打开原实例"
                : "打开目标父目录"
        };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(path);
                Feedback.Text = "路径已复制。";
            }
        };
        open.Click += async (_, _) =>
        {
            if (!Directory.Exists(folder))
            {
                Feedback.Text = "目录尚不存在：" + folder;
                return;
            }

            try
            {
                var directory = await StorageProvider.TryGetFolderFromPathAsync(
                    new Uri(folder)
                );
                if (directory is null || !await Launcher.LaunchFileAsync(directory))
                {
                    Feedback.Text = "无法打开目录，可复制路径后在 Finder 中前往。";
                }
                else
                {
                    Feedback.Text = "已打开：" + folder;
                }
            }
            catch (Exception ex)
            {
                Feedback.Text = ex.Message;
            }
        };
        row.Children.Add(copy);
        row.Children.Add(open);
        panel.Children.Add(row);
    }
}

public sealed record CopyConfirmation(bool Accepted, bool Suppress);
