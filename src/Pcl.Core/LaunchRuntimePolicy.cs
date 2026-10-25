namespace Pcl.Core;
/// <summary>将已保存的自动内存与 GC 选择解析为当前 Java 真正支持的参数。</summary>
public static class LaunchRuntimePolicy
{
    // [实现] ResolveMemory 的实现：按 !settings.AutomaticMemory 分支处理；准备 var mb = availableBytes > 0 ? availableBytes / 1048576 / 2 : 2048；准备 var maximum = (int)Math.Clamp(mb, 512, 8192)。
    public static LaunchSettings ResolveMemory(LaunchSettings settings, long availableBytes)
    {
        if (!settings.AutomaticMemory)
        {
            // [实现] 条件成立（!settings.AutomaticMemory）时：返回 settings。
            return settings;
        }

        var mb = availableBytes > 0
            ? availableBytes / 1048576 / 2
            : 2048;
        var maximum = (int)Math.Clamp(mb, 512, 8192);
        return settings with
        {
            MinimumMemoryMb = Math.Min(settings.MinimumMemoryMb, maximum),
            MaximumMemoryMb = maximum
        };
    }

    // JDK 21–22 需要显式开启分代；23 默认分代，24 起移除旧模式，不能继续传已移除的标志。
    public static string[] CollectorArguments(string mode, int javaMajor) => mode switch
    {
        "default" => [],
        "g1" => [
            "-XX:+UseG1GC"
        ],
        "g1-tuned" => [
            "-XX:+UseG1GC",
            "-XX:MaxGCPauseMillis=50",
            "-XX:G1ReservePercent=20"
        ],
        "zgc" when javaMajor >= 17 => [
            "-XX:+UseZGC"
        ],
        "generational" when javaMajor >= 23 => [
            "-XX:+UseZGC"
        ],
        "generational" when javaMajor >= 21 => [
            "-XX:+UseZGC",
            "-XX:+ZGenerational"
        ],
        "zgc" or "generational" => [
            "-XX:+UseG1GC"
        ],
        _ => throw new InvalidDataException("不支持的内存管理模式。")
    };
}
