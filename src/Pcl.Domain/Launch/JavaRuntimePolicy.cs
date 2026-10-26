namespace Pcl.Domain;
public sealed record MemoryBounds(int MinimumMb, int MaximumMb);
// 只计算给定内存及 Java 版本的启动策略，不探测物理内存或运行 Java。
public static class JavaRuntimePolicy
{
    public static MemoryBounds ResolveMemory(int minimumMb, int maximumMb, bool automatic, long availableBytes)
    {
        if (!automatic)
        {
            return new(minimumMb, maximumMb);
        }

        var mb = availableBytes > 0
            ? availableBytes / 1048576 / 2
            : 2048;
        var maximum = (int)Math.Clamp(mb, 512, 8192);
        return new(
            Math.Min(minimumMb, maximum),
            maximum
        );
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
