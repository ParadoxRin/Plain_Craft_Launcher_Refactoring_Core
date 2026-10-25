using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Pcl.Desktop;
/// <summary>所有新增淡入统一使用速度参数；重复导航取消旧动画，速度零立即完成。</summary>
internal static class UiMotion
{
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> Active = new();
    internal static double Speed { get; private set; } = 1;

    internal static void Configure(double speed)
    {
        Speed = speed;
        foreach (var item in Active)
        {
            item.Value.Cancel();
            item.Key.Opacity = 1;
        }

        Active.Clear();
    }

    internal static async Task FadeIn(Control control, double milliseconds = 180)
    {
        if (Active.TryGetValue(control, out var previous))
        {
            previous.Cancel();
            Active.Remove(control);
        }

        if (Speed <= 0)
        {
            control.Opacity = 1;
            return;
        }

        using var lifetime = new CancellationTokenSource();
        Active.Add(control, lifetime);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var duration = milliseconds / Speed;
        control.Opacity = 0;
        try
        {
            while (watch.Elapsed.TotalMilliseconds < duration)
            {
                control.Opacity = 1 - Math.Pow(
                    1 - Math.Min(1, watch.Elapsed.TotalMilliseconds / duration),
                    3
                );
                await Task.Delay(16, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (Active.TryGetValue(control, out var current) && ReferenceEquals(current, lifetime))
            {
                control.Opacity = 1;
                Active.Remove(control);
            }
        }
    }
}
