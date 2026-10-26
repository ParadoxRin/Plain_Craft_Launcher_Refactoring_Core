using System.Runtime.InteropServices;

namespace Pcl.Core.Adapters.Platform;
// 仅在组装/适配边界探测宿主，业务规则接收 TargetPlatform 值。
public static class HostPlatform
{
    public static TargetPlatform Detect() => new(
        OperatingSystem.IsMacOS()
            ? "osx"
            : OperatingSystem.IsWindows()
                ? "windows"
                : "linux",
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64"
            : "amd64",
        Environment.OSVersion.Version.ToString()
    );
}
