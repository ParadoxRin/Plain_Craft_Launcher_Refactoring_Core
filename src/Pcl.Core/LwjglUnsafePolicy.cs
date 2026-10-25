using Newtonsoft.Json.Linq;
using System.Security.Cryptography;

namespace Pcl.Core;
// 只对开发者明确支持的 Java/LWJGL 组合加载固定版本 agent；禁用项在任何文件检查之前生效。
public static class LwjglUnsafePolicy
{
    public const string Sha256 = "6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015";
    // [实现] Applies 的实现：计算并返回 !settings.DisableLwjglUnsafeAgent && javaMajor >= 25 && (metadata["libraries"] as JArray ?? [])…。
    public static bool Applies(LaunchSettings settings, int javaMajor, JObject metadata) => !settings.DisableLwjglUnsafeAgent && javaMajor >= 25 && (metadata["libraries"] as JArray ?? []).Any(
        x => (string? )x["name"] is "org.lwjgl:lwjgl:3.4.0" or "org.lwjgl:lwjgl:3.4.1"
    );
    // [实现] Arguments 的实现：按 !Applies(settings, javaMajor, metadata) 分支处理；准备 var file = Path.Combine(AppContext.BaseDirectory, "lwjgl-unsafe-agent-2.0.jar")；按 !File.Exists(file) || Convert.ToHexString( SHA256.HashData( File.ReadAllBytes(file) ) ) .ToLowe… 分支处理。
    public static string[] Arguments(LaunchSettings settings, int javaMajor, JObject metadata)
    {
        if (!Applies(settings, javaMajor, metadata))
        {
            // [实现] 条件成立（!Applies(settings, javaMajor, metadata)）时：返回 []。
            return[];
        }

        var file = Path.Combine(AppContext.BaseDirectory, "lwjgl-unsafe-agent-2.0.jar");
        if (!File.Exists(file) || Convert.ToHexString(
            SHA256.HashData(
                File.ReadAllBytes(file)
            )
        )
            .ToLowerInvariant() != Sha256)
        {
            // [实现] 条件成立（!File.Exists(file) || Convert.ToHexString( SHA256.HashData( File.ReadAllBytes(file) ) ) .ToLowe…）时：抛出 new InvalidDataException("LWJGL 优化组件缺失或校验失败。请修复应用，或在启动设置中禁用此组件。")。
            throw new InvalidDataException("LWJGL 优化组件缺失或校验失败。请修复应用，或在启动设置中禁用此组件。");
        }

        return[
            "-javaagent:" + file
        ];
    }
}
