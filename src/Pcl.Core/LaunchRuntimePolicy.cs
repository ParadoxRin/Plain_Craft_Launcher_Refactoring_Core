namespace Pcl.Core;
// 兼容已有设置模型；纯策略由 Domain 计算，避免多个安装/启动入口重新实现规则。
public static class LaunchRuntimePolicy
{
    public static LaunchSettings ResolveMemory(LaunchSettings settings, long availableBytes)
    {
        var bounds = Pcl.Domain.JavaRuntimePolicy.ResolveMemory(
            settings.MinimumMemoryMb,
            settings.MaximumMemoryMb,
            settings.AutomaticMemory,
            availableBytes
        );
        return settings with
        {
            MinimumMemoryMb = bounds.MinimumMb,
            MaximumMemoryMb = bounds.MaximumMb
        };
    }

    public static string[] CollectorArguments(string mode, int javaMajor) => Pcl.Domain.JavaRuntimePolicy.CollectorArguments(mode, javaMajor);
}
