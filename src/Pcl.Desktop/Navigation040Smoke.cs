using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 在隔离数据根运行真实 Avalonia 窗口及 Host；可控慢任务用于稳定复现关闭竞态。
    private async Task Navigation040SmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--navigation040-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            File.AppendAllText(report + ".progress", name + "\n");
        }

        async Task Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!condition())
            {
                if (DateTime.UtcNow >= until)
                {
                    throw new TimeoutException("navigation condition");
                }

                await Task.Delay(30);
            }
        }

        try
        {
            UiMotion.Configure(0);
            var page = new Lifecycle040Page("same");
            var shown = page.PresentAsync(this);
            await Wait(
                () => page.IsVisible
            );
            page.Draft.Text = "retained draft";
            var duplicate = new Lifecycle040Page("same");
            var duplicateShown = duplicate.PresentAsync(this);
            await Task.Delay(80);
            Check(
                !duplicate.IsVisible && duplicate.CleanupCount == 1 && page.Draft.Text == "retained draft",
                "same-page-reuses-live-state"
            );
            var other = new Lifecycle040Page("other");
            var otherShown = other.PresentAsync(this);
            await Wait(
                () => other.IsVisible
            );
            Check(page.IsVisible && other.IsVisible && IsEnabled, "different-context-pages-nonmodal");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = page.HoldAsync(release);
            page.Close();
            await Task.Delay(80);
            Check(
                page.IsVisible && page.CleanupCount == 0 && page.CancelCount == 0,
                "close-waits-without-cancelling-host-work"
            );
            release.SetResult();
            await Task.WhenAll(work, shown, duplicateShown)
                .WaitAsync(
                    TimeSpan.FromSeconds(10)
                );
            Check(page.CleanupCount == 1 && !page.IsVisible, "close-finishes-automatically-after-work");
            await other.RequestCloseAsync();
            await otherShown;
            var parent = new Lifecycle040Page("parent");
            var parentShown = parent.PresentAsync(this);
            var child = new Lifecycle040Page("child");
            var childShown = child.PresentAsync(parent);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var childWork = child.HoldAsync(pending);
            var order = new List<string>();
            child.ViewLifetime.OnStop(
                () =>
                {
                    order.Add("child");
                    return Task.CompletedTask;
                }
            );
            parent.ViewLifetime.OnStop(
                () =>
                {
                    order.Add("parent");
                    return Task.CompletedTask;
                }
            );
            var stop = parent.RequestCloseAsync(true);
            await Task.WhenAll(stop, parentShown, childShown, childWork)
                .WaitAsync(
                    TimeSpan.FromSeconds(10)
                );
            Check(
                child.CancelCount == 1 && order.SequenceEqual(
                    new[]
                    {
                        "child",
                        "parent"
                    }
                ),
                "parent-exit-drains-child-before-own-release"
            );
            await parent.RequestCloseAsync(true);
            Check(parent.CleanupCount == 1 && child.CleanupCount == 1, "repeated-close-releases-once");
            // 先关闭父页再退出应用：已有等待链必须升级取消到子页，不能只取消父页自身。
            var escalatingParent = new Lifecycle040Page("escalating-parent");
            var escalatingShown = escalatingParent.PresentAsync(this);
            var escalatingChild = new Lifecycle040Page("escalating-child");
            var escalatingChildShown = escalatingChild.PresentAsync(escalatingParent);
            var childRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var escalatingWork = escalatingChild.HoldAsync(childRelease);
            var ordinaryClose = escalatingParent.RequestCloseAsync();
            await Task.Delay(80);
            Check(
                !ordinaryClose.IsCompleted && escalatingChild.CancelCount == 0,
                "parent-ordinary-close-preserves-child-work"
            );
            await Task.WhenAll(
                escalatingParent.RequestCloseAsync(true),
                ordinaryClose,
                escalatingWork,
                escalatingShown,
                escalatingChildShown
            )
                .WaitAsync(
                    TimeSpan.FromSeconds(10)
                );
            Check(
                escalatingChild.CancelCount == 1 && escalatingParent.CleanupCount == 1 && escalatingChild.CleanupCount == 1,
                "application-exit-escalates-pending-child-close"
            );
            // 真正需要返回选择的窗口保持模态；关闭结果不能被异步生命周期吞掉。
            var dialog = new WorkspaceWindow
            {
                Title = "确认测试",
                Content = new TextBlock
                {
                    Text = "result"
                }
            };
            var answer = dialog.PresentAsync<bool>(this);
            dialog.Close(true);
            Check(await answer, "typed-confirmation-result-preserved");
            // 使用真实 Host 页面读取空账户与实例；重复打开保留搜索输入。
            var instances = Bootstrap.WorkspacePages.Instances(this, service);
            var instancesShown = instances.PresentAsync(this);
            await Wait(
                () => instances.IsVisible && !instances.Busy
            );
            var search = instances.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.PlaceholderText == "搜索实例"
                );
            search.Text = "persistent-filter";
            var secondInstances = Bootstrap.WorkspacePages.Instances(this, service);
            var secondShown = secondInstances.PresentAsync(this);
            await Task.Delay(80);
            Check(
                search.Text == "persistent-filter" && !secondInstances.IsVisible,
                "real-instance-page-preserves-search"
            );
            await instances.RequestCloseAsync(true);
            await Task.WhenAll(instancesShown, secondShown);
            var skins = Bootstrap.WorkspacePages.Skins(this, accounts!);
            var skinsShown = skins.PresentAsync(this);
            await Wait(
                () => skins.IsVisible && !skins.Busy
            );
            await skins.RequestCloseAsync(true);
            await skinsShown;
            Check(skins.ClosedTask.IsCompletedSuccessfully, "real-skin-page-load-and-close-without-account");
            var center = new TaskCenterWindow(service, RunAsync, Open);
            var centerShown = center.PresentAsync(this);
            await Task.Delay(500);
            await center.RequestCloseAsync(true)
                .WaitAsync(
                    TimeSpan.FromSeconds(10)
                );
            await centerShown;
            Check(center.ClosedTask.IsCompletedSuccessfully, "real-task-center-stops-refresh-before-release");
            await NavigateClassicAsync("more");
            Check(
                !classicNavigation.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        x => x.Text == "百宝箱"
                    ),
                "removed-toolbox-not-restored"
            );
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        finally
        {
            await RequestApplicationExitAsync();
        }
    }

    private sealed class Lifecycle040Page : WorkspaceWindow, IHostWorkspace
    {
        internal readonly TextBox Draft = new();
        internal int CleanupCount;
        internal int CancelCount;
        internal Lifecycle040Page(string context)
        {
            NavigationContext = context;
            Title = "生命周期验证";
            Width = 760;
            Height = 300;
            Content = Draft;
            ViewLifetime.OnStop(
                () =>
                {
                    CleanupCount++;
                    return Task.CompletedTask;
                }
            );
        }

        internal async Task HoldAsync(TaskCompletionSource release)
        {
            using var activity = ViewLifetime.Begin(
                () =>
                {
                    CancelCount++;
                    release.TrySetResult();
                }
            );
            await release.Task;
        }
    }
}
