using System.Reflection;
using Newtonsoft.Json.Linq;
using Pcl.Legacy;

namespace Pcl.Core;
// Mac 运行环境适配：匹配 Java 版本和 CPU 架构，检查原生库与旧版差异，生成适用于当前平台的启动条件。
public static class LegacyMacLibraries
{
    // [实现] Adapt 的实现：准备 var result = (JObject)original.DeepClone()；按 platform.Os != "osx" || platform.Architecture != "aarch64" 分支处理；准备 var stream = typeof(LegacyMacLibraries).Assembly.GetManifestResourceStream("Pcl.Core.Compatibil…。
    public static JObject Adapt(JObject original, TargetPlatform platform)
    {
        var result = (JObject)original.DeepClone();
        if (platform.Os != "osx" || platform.Architecture != "aarch64")
            return result;
        using var stream = typeof(LegacyMacLibraries).Assembly.GetManifestResourceStream("Pcl.Core.Compatibility.xmcl-macos-arm64.json")!;
        using var reader = new StreamReader(stream);
        var map = JObject.Parse(
            reader.ReadToEnd()
        );
        using var jnaStream = typeof(LegacyMacLibraries).Assembly.GetManifestResourceStream("Pcl.Core.Compatibility.jna-arm64.json")!;
        using var jnaReader = new StreamReader(jnaStream);
        var jna = JObject.Parse(
            jnaReader.ReadToEnd()
        );
        var libs = new List<JObject>();
        var changed = new List<string>();
        foreach (var lib in original["libraries"]!.OfType<JObject>())
        {
            // [实现] 对 original["libraries"]!.OfType<JObject>() 中的每一项：按 !MinecraftRules.Check( lib["rules"], platform.Os, platform.Architecture, platform.OsVersion, ne… 分支处理；准备 var name = (string)lib["name"]!；准备 var copy = (JObject)lib.DeepClone()。
            if (!MinecraftRules.Check(
                lib["rules"],
                platform.Os,
                platform.Architecture,
                platform.OsVersion,
                new Dictionary<string, bool>()
            ))
                continue;
            var name = (string)lib["name"]!;
            var copy = (JObject)lib.DeepClone();
            if (map[name] is JObject replacement)
            {
                // [实现] 条件成立（map[name] is JObject replacement）时：执行 copy = (JObject)replacement.DeepClone()；执行 changed.Add(name)。
                copy = (JObject)replacement.DeepClone();
                changed.Add(name);
            }
            else if (jna[name] is JObject newer)
            {
                // [实现] 条件成立（jna[name] is JObject newer）时：执行 copy = (JObject)newer.DeepClone()；执行 changed.Add(name)。
                copy = (JObject)newer.DeepClone();
                changed.Add(name);
            }

            // LWJGL 2 has a native-only replacement. Null mappings explicitly remove unsupported
            // optional JInput and narrator binaries; they must not retain the Intel classifier.
            if (lib["natives"]?["osx"] is not null && map.Property(name + ":natives")is { } nativeMapping)
            {
                // [实现] 条件成立（lib["natives"]?["osx"] is not null && map.Property(name + ":natives")is { } nativeMapping）时：执行 copy.Remove("natives")；执行 copy.Remove("extract")；按 copy["downloads"]?["artifact"] is not null 分支处理。
                copy.Remove("natives");
                copy.Remove("extract");
                if (copy["downloads"]?["artifact"] is not null)
                    libs.Add(copy);
                if (nativeMapping.Value is JObject native)
                    libs.Add(
                        (JObject)native.DeepClone()
                    );
                changed.Add(name + ":natives");
            }
            else
                libs.Add(copy);
        }

        result["libraries"] = new JArray(
            libs.DistinctBy(
                x => (string? )x["name"]
            )
        );
        if (changed.Count > 0)
            result["pclMacCompatibility"] = new JObject
            {
                ["source"] = "XMCL 029f9ba + Mojang 1.20.1 JNA metadata",
                ["replaced"] = new JArray(
                    changed.Distinct()
                )
            };
        return result;
    }
}
