using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 实例分组桌面验收：在隔离数据目录使用真实控件保存、筛选、重开和清除分组，记录结果与截图。
public partial class MainWindow
{
    /// <summary>在隔离数据目录通过实际控件保存、筛选、重开和清除分组；不启动或模拟游戏。</summary>
    private async Task InstanceGroupsSmokeAsync()
    {
        try
        {
            // 仅允许空验收目录，防止固定夹具覆盖用户已存在的实例。
            var instances = Path.Combine(paths.Root, "instances");
            if (Directory.Exists(instances) && Directory.EnumerateFileSystemEntries(instances)
                .Any())
            {
                // [实现] 条件成立（Directory.Exists(instances) && Directory.EnumerateFileSystemEntries(instances) .Any()）时：抛出 new InvalidOperationException("分组验收需要空实例目录。")。
                throw new InvalidOperationException("分组验收需要空实例目录。");
            }

            foreach (var id in new[]
            {
                "group-a",
                "group-b"
            }

            )
            {
                // [实现] 对 new[] { "group-a", "group-b" } 中的每一项：准备 var folder = Path.Combine(instances, id)；执行 Directory.CreateDirectory(folder)；执行 JsonFiles.Write( Path.Combine(folder, "profile.json"), new { id, gameVersion = "1.20.1", loader…。
                var folder = Path.Combine(instances, id);
                Directory.CreateDirectory(folder);
                JsonFiles.Write(
                    Path.Combine(folder, "profile.json"),
                    new
                    {
                        id,
                        gameVersion = "1.20.1",
                        loader = "fabric",
                        displayName = "分组验收 " + id
                    }
                );
            }

            var target = Path.Combine(instances, "group-a");
            var store = new LauncherSettingsStore(paths);
            // 每次创建全新窗口，读取磁盘状态；通过产品按钮而非直接写设置完成修改。
            async Task Exercise(Func<InstanceWorkspace, Task> action)
            {
                Exception? failure = null;
                var window = Bootstrap.WorkspacePages.Instances(this, service, "group-a");
                window.ShowActivated = false;
                window.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(250)；执行 await action(window)。
                        await Task.Delay(250);
                        await action(window);
                    }
                    catch (Exception ex)
                    {
                        // [实现] 捕获 (Exception ex) 后：执行 failure = ex。
                        failure = ex;
                    }
                    finally
                    {
                        // [实现] 无论成功或失败，退出前：执行 window.Close()。
                        window.Close();
                    }
                };
                await window.ShowDialog(this);
                if (failure is not null)
                {
                    // [实现] 条件成立（failure is not null）时：抛出 failure。
                    throw failure;
                }
            }

            // [实现] Find 的实现：计算并返回 window.GetVisualDescendants() .OfType<T>() .Single( control => control.Name == name )。
            static T Find<T>(Window window, string name)
            where T : Control => window.GetVisualDescendants()
                .OfType<T>()
                .Single(
                    control => control.Name == name
                );
            // [实现] Save 的实现：执行 window.GetVisualDescendants() .OfType<Button>() .Single( button => button.Content?.ToString() =…。
            static void Save(Window window) => window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    button => button.Content?.ToString() == "保存"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Exercise(
                async window =>
                {
                    // [实现] 回调触发时：执行 Find<TextBox>(window, "InstanceGroupName") .Text = " 生存世界 "；执行 Save(window)；执行 await Task.Delay(150)。
                    Find<TextBox>(window, "InstanceGroupName")
                        .Text = "  生存世界  ";
                    Save(window);
                    await Task.Delay(150);
                    if (store.Preferences(target)
                        .Group != "生存世界")
                    {
                        // [实现] 条件成立（store.Preferences(target) .Group != "生存世界"）时：抛出 new InvalidOperationException("分组保存失败。")。
                        throw new InvalidOperationException("分组保存失败。");
                    }

                    var filter = Find<ComboBox>(window, "InstanceGroupFilter");
                    filter.SelectedItem = filter.Items.Cast<object>()
                        .Single(
                            item => item.ToString() == "分组 · 生存世界"
                        );
                    var list = Find<ListBox>(window, "InstanceList");
                    if (list.Items.Count != 1 || (list.SelectedItem as GameProfile)?.Id != "group-a")
                    {
                        // [实现] 条件成立（list.Items.Count != 1 || (list.SelectedItem as GameProfile)?.Id != "group-a"）时：抛出 new InvalidOperationException("分组过滤结果不正确。")。
                        throw new InvalidOperationException("分组过滤结果不正确。");
                    }

                    using var bitmap = new RenderTargetBitmap(
                        new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                        new Vector(96, 96)
                    );
                    bitmap.Render(window);
                    bitmap.Save(
                        Path.Combine(paths.Evidence, "instance-groups.png"),
                        PngBitmapEncoderOptions.Default
                    );
                }
            );
            await Exercise(
                async window =>
                {
                    // [实现] 回调触发时：准备 var field = Find<TextBox>(window, "InstanceGroupName")；按 field.Text != "生存世界" 分支处理；执行 field.Text = ""。
                    var field = Find<TextBox>(window, "InstanceGroupName");
                    if (field.Text != "生存世界")
                    {
                        // [实现] 条件成立（field.Text != "生存世界"）时：抛出 new InvalidOperationException("重开窗口后分组丢失。")。
                        throw new InvalidOperationException("重开窗口后分组丢失。");
                    }

                    field.Text = "";
                    Save(window);
                    await Task.Delay(100);
                    if (store.Preferences(target)
                        .Group != "" || new InstanceManager(service).Groups()
                        .Count != 0)
                    {
                        // [实现] 条件成立（store.Preferences(target) .Group != "" || new InstanceManager(service).Groups() .Count != 0）时：抛出 new InvalidOperationException("清除分组后存在残留。")。
                        throw new InvalidOperationException("清除分组后存在残留。");
                    }
                }
            );
            await Exercise(
                async window =>
                {
                    // 验证 Quick Play 字段通过相同的逐字段继承编辑器保存，而不是直接修改 JSON。
                    window.GetVisualDescendants()
                        .OfType<Button>()
                        .Single(
                            button => button.Content?.ToString() == "启动设置"
                        )
                        .RaiseEvent(
                            new RoutedEventArgs(Button.ClickEvent)
                        );
                    Find<CheckBox>(window, "Override_QuickPlayWorld")
                        .IsChecked = true;
                    Find<TextBox>(window, "Launch_QuickPlayWorld")
                        .Text = "Demo_World";
                    window.GetVisualDescendants()
                        .OfType<Button>()
                        .Single(
                            button => button.Content?.ToString() == "保存启动设置"
                        )
                        .RaiseEvent(
                            new RoutedEventArgs(Button.ClickEvent)
                        );
                    await Task.Delay(100);
                    if (new LauncherSettingsStore(paths).Effective(target)
                        .QuickPlayWorld != "Demo_World")
                    {
                        // [实现] 条件成立（new LauncherSettingsStore(paths).Effective(target) .QuickPlayWorld != "Demo_World"）时：抛出 new InvalidOperationException("Quick Play 字段没有持久化。")。
                        throw new InvalidOperationException("Quick Play 字段没有持久化。");
                    }
                }
            );
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "instance-groups.json"),
                new
                {
                    status = "passed",
                    savedViaButton = true,
                    filteredViaControl = true,
                    reopenedWindow = true,
                    clearedViaButton = true,
                    isolatedFixtures = true,
                    quickPlaySettingSavedViaControl = true
                }
            );
            Console.WriteLine("INSTANCE_GROUPS_UI_PASSED");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 Console.Error.WriteLine("INSTANCE_GROUPS_UI_FAILED " + ex)；执行 Environment.ExitCode = 1。
            Console.Error.WriteLine("INSTANCE_GROUPS_UI_FAILED " + ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
