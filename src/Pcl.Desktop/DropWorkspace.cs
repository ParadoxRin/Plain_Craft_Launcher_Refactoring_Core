using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 设置和个性化：读取、校验并应用用户配置，更新界面或处理本地文件入口，保存可恢复设置。
public partial class MainWindow
{
    // [实现] EnableFileDrop 的实现：执行 DragDrop.SetAllowDrop(this, true)；执行 AddHandler( DragDrop.DragOverEvent, (_, e) => { e.DragEffects = operation is null && game is nu…；执行 AddHandler( DragDrop.DropEvent, async (_, e) => { if (operation is not null || game is not null…。
    private void EnableFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(
            DragDrop.DragOverEvent,
            (_, e) =>
            {
                // [实现] 回调触发时：执行 e.DragEffects = operation is null && game is null && e.DataTransfer.TryGetFiles()?.Count() == 1…；执行 e.Handled = true。
                e.DragEffects = operation is null && game is null && e.DataTransfer.TryGetFiles()?.Count() == 1
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                e.Handled = true;
            }
        );
        AddHandler(
            DragDrop.DropEvent,
            async (_, e) =>
            {
                // [实现] 回调触发时：按 operation is not null || game is not null 分支处理；准备 var files = e.DataTransfer.TryGetFiles()?.ToArray()；按 files?.Length != 1 || files[0].TryGetLocalPath()is not string path 分支处理。
                if (operation is not null || game is not null)
                    return;
                var files = e.DataTransfer.TryGetFiles()?.ToArray();
                if (files?.Length != 1 || files[0].TryGetLocalPath()is not string path)
                    return;
                e.Handled = true;
                await ImportDroppedAsync(path);
            }
        );
    }

    // [实现] ImportDroppedAsync 的实现：执行受异常处理保护的操作。
    private async Task ImportDroppedAsync(string path)
    {
        try
        {
            // [实现] 在异常保护范围内：按 !File.Exists(path) || ProfileBox.SelectedItem is not GameProfile profile 分支处理；准备 var extension = Path.GetExtension(path) .ToLowerInvariant()；按 extension is not (".jar" or ".litemod" or ".zip" or ".mrpack") 分支处理。
            if (!File.Exists(path) || ProfileBox.SelectedItem is not GameProfile profile)
                throw new InvalidOperationException("先选择实例，再拖入一个模组或压缩包。");
            var classified = await DesktopApi.CallAsync<DropClassification>(
                "workspace.shell.classifyDrop",
                null,
                new
                {
                    path
                }
            );
            ResourceKind? kind = classified.Kind is null
                ? null
                : Enum.Parse<ResourceKind>(classified.Kind);
            if (kind is null)
            {
                // [实现] 条件成立（kind is null）时：准备 var manager = new PackManagerWindow( service, profile, p => RefreshProfiles(p.Id) )；执行 await manager.SelectArchiveAsync(path)；执行 await manager.ShowDialog(this)。
                var manager = Bootstrap.WorkspacePages.Pack(
                    this,
                    service,
                    profile,
                    p => RefreshProfiles(p.Id)
                );
                await manager.SelectArchiveAsync(path);
                await manager.ShowDialog(this);
                return;
            }

            var dialog = new WorkspaceWindow
            {
                Title = "导入本地资源",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var panel = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 16
            };
            panel.Children.Add(
                new TextBlock
                {
                    Text = Path.GetFileName(path) + "\n导入到：" + profile.Name,
                    TextWrapping = TextWrapping.Wrap
                }
            );
            var confirm = new Button
            {
                Content = "检查并导入"
            };
            confirm.Click += (_, _) => dialog.Close(true);
            panel.Children.Add(confirm);
            dialog.Content = panel;
            if (await dialog.ShowDialog<bool>(this))
                await RunAsync(
                    async ct =>
                    {
                        // [实现] 回调触发时：准备 var result = await new ResourceInstaller(profile.GameDirectory, service.Downloads, profile.Load…；执行 StatusText.Text = result.Name + " 已导入"。
                        var result = await DesktopApi.CallAsync<ManagedResource>(
                            "workspace.browser.import",
                            profile.Id,
                            new
                            {
                                path
                            },
                            ct
                        );
                        StatusText.Text = result.Name + " 已导入";
                    }
                );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 StatusText.Text = ex.Message。
            StatusText.Text = ex.Message;
        }
    }
}
