namespace Pcl.Core;
// 游戏目标平台是显式输入；此值类型不读取宿主系统或进程架构。
public sealed record TargetPlatform(string Os, string Architecture, string OsVersion)
{
    public string Separator => Os == "windows"
        ? ";"
        : ":";
}
