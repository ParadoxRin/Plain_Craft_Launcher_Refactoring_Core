using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Pcl.Desktop;
// 一个主窗口拥有一个导航登记表；管理页非模态复用，返回结果的确认窗仍为模态。
internal sealed class WindowNavigation
{
    private static readonly ConditionalWeakTable<Window, WindowNavigation> Roots = new();
    private readonly Dictionary<(Type Type, string Context), WorkspaceWindow> pages = new();
    private bool stopping;
    internal static WindowNavigation For(Window owner)
    {
        while (owner.Owner is Window parent)
        {
            owner = parent;
        }

        return Roots.GetValue(
            owner,
            _ => new WindowNavigation()
        );
    }

    internal async Task OpenAsync(WorkspaceWindow page, Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (stopping)
        {
            await page.DiscardAsync();
            return;
        }

        var key = (page.GetType(), page.NavigationContext);
        if (pages.TryGetValue(key, out var existing))
        {
            await page.DiscardAsync();
            if (!existing.ViewClosing)
            {
                existing.Activate();
            }

            await existing.ClosedTask;
            return;
        }

        pages.Add(key, page);
        try
        {
            try
            {
                page.Show(owner);
            }
            catch
            {
                await page.DiscardAsync();
                throw;
            }

            await page.ClosedTask;
        }
        finally
        {
            pages.Remove(key);
        }
    }

    internal async Task StopAsync(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        stopping = true;
        await CloseChildrenAsync(owner, true);
    }

    internal static Task CloseChildrenAsync(Window owner, bool cancelWork)
    {
        return Task.WhenAll(
            owner.OwnedWindows.ToArray()
                .Select(
                    async child =>
                    {
                        if (child is WorkspaceWindow page)
                        {
                            await page.RequestCloseAsync(cancelWork);
                        }
                        else
                        {
                            await CloseChildrenAsync(child, cancelWork);
                            child.Close();
                        }
                    }
                )
        );
    }
}

internal static class WindowPresentation
{
    // 只有管理页面走导航复用；提示、预览和选择窗保留独立对话框语义。
    internal static Task PresentAsync(this Window window, Window owner)
    {
        return window is WorkspaceWindow page && window is IHostWorkspace
            ? WindowNavigation.For(owner)
                .OpenAsync(page, owner)
            : window.ShowDialog(owner);
    }

    internal static Task<T> PresentAsync<T>(this Window window, Window owner)
    {
        if (window is IHostWorkspace)
        {
            throw new InvalidOperationException("管理页面不能作为返回结果的模态对话框。");
        }

        return window.ShowDialog<T>(owner);
    }
}
