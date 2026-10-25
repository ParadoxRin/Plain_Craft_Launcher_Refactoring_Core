using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Pcl.Core;
// [实现] 数据约定：NativeBinaryAudit 汇集 Archive、Entry、HasArm64、Applicable，供该模块传递状态。
public sealed record NativeBinaryAudit(string Archive, string Entry, bool HasArm64, bool Applicable = true);
// Mac 运行环境适配：匹配 Java 版本和 CPU 架构，检查原生库与旧版差异，生成适用于当前平台的启动条件。
public static class MacNativeAudit
{
    // [实现] HasArm64 的实现：按 header.Length < 8 分支处理；准备 var magic = BinaryPrimitives.ReadUInt32BigEndian(header)；准备 uint arm64 = 0x0100000c。
    public static bool HasArm64(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
            return false;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        const uint arm64 = 0x0100000c;
        if (magic is 0xfeedfacf or 0xfeedface)
            return BinaryPrimitives.ReadUInt32BigEndian(header[4..]) == arm64;
        if (magic is 0xcffaedfe or 0xcefaedfe)
            return BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == arm64;
        if (magic is not (0xcafebabe or 0xcafebabf or 0xbebafeca or 0xbfbafeca))
            return false;
        var little = magic is 0xbebafeca or 0xbfbafeca;
        var count = little
            ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (count > 32)
            return false;
        var stride = magic is 0xcafebabf or 0xbfbafeca
            ? 32
            : 20;
        for (var i = 0; i < count; i++)
        {
            // [实现] 每次迭代（i < count）：准备 var offset = 8 + (int)i * stride；按 offset + 4 > header.Length 分支处理；准备 var cpu = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]) : BinaryPrimitives…。
            var offset = 8 + (int)i * stride;
            if (offset + 4 > header.Length)
                return false;
            var cpu = little
                ? BinaryPrimitives.ReadUInt32LittleEndian(header[offset..])
                : BinaryPrimitives.ReadUInt32BigEndian(header[offset..]);
            if (cpu == arm64)
                return true;
        }

        return false;
    }

    // [实现] IsForeignMacScope 的实现：计算并返回 Regex.IsMatch( path, @"(?:^|/)(?:(?:macos|osx|darwin|macosx)/(?:x64|x86|x86_64|amd64|i386|ppc|p…。
    public static bool IsForeignMacScope(string path) => Regex.IsMatch(
        path,
        @"(?:^|/)(?:(?:macos|osx|darwin|macosx)/(?:x64|x86|x86_64|amd64|i386|ppc|ppc64|arm|arm32)(?:/|$)|(?:darwin|macosx|osx|macos)-(?:x86[-_]64|x64|x86|amd64|i386|ppc)(?:/|$))",
        RegexOptions.IgnoreCase
    );
    // [实现] Check 的实现：按 java.Architecture != "aarch64" || java.MajorVersion != version.JavaMajor 分支处理；准备 var result = new List<NativeBinaryAudit>()；遍历 version.Libraries.Where( x => x.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ) 处理 item。
    public static IReadOnlyList<NativeBinaryAudit> Check(InstalledVersion version, JavaRuntime java)
    {
        if (java.Architecture != "aarch64" || java.MajorVersion != version.JavaMajor)
            throw new InvalidDataException("Java 主版本或 ARM64 架构与游戏不匹配。");
        var result = new List<NativeBinaryAudit>();
        foreach (var item in version.Libraries.Where(
            x => x.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
        ))
        {
            // [实现] 对 version.Libraries.Where( x => x.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ) 中的每一项：按 Regex.IsMatch( Path.GetFileName(item.Path), @"-natives-(?:windows|linux|freebsd|android)(?:[-.]… 分支处理；准备 var zip = ZipFile.OpenRead(item.Path)；遍历 zip.Entries.Where( x => x.FullName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || x.… 处理 entry。
            if (Regex.IsMatch(
                Path.GetFileName(item.Path),
                @"-natives-(?:windows|linux|freebsd|android)(?:[-.]|$)",
                RegexOptions.IgnoreCase
            ))
                throw new InvalidDataException("混入了其他平台的原生库：" + item.Path);
            using var zip = ZipFile.OpenRead(item.Path);
            foreach (var entry in zip.Entries.Where(
                x => x.FullName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".jnilib", StringComparison.OrdinalIgnoreCase)
            ))
            {
                // [实现] 对 zip.Entries.Where( x => x.FullName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || x.… 中的每一项：按 entry.FullName.Split('/') .Any( x => x.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase) ) 分支处理；准备 var file = Path.GetFileName(item.Path)；准备 var pairedJTracy = file.StartsWith("jtracy-") && file.EndsWith("-natives-macos.jar") && entry.F…。
                if (entry.FullName.Split('/')
                    .Any(
                        x => x.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase)
                    ))
                    continue;
                // Netty embeds architecture-qualified optional transports; JTracy ships separate x64 and ARM jars.
                var file = Path.GetFileName(item.Path);
                var pairedJTracy = file.StartsWith("jtracy-") && file.EndsWith("-natives-macos.jar") && entry.FullName == "libjtracy-jni-macos.dylib" && version.Libraries.Any(
                    x => Path.GetFileName(x.Path) == file.Replace("-natives-macos.jar", "-natives-macos-arm64.jar")
                );
                var foreignNetty = Regex.IsMatch(entry.FullName, @"(?:^|/)libnetty_[^/]*_(?:x86_64|x86|amd64)\.(?:jnilib|dylib)$");
                if (IsForeignMacScope(entry.FullName) || pairedJTracy || foreignNetty)
                {
                    // [实现] 条件成立（IsForeignMacScope(entry.FullName) || pairedJTracy || foreignNetty）时：执行 result.Add( new(item.Path, entry.FullName, false, false) )；执行 continue;。
                    result.Add(
                        new(item.Path, entry.FullName, false, false)
                    );
                    continue;
                }

                using var stream = entry.Open();
                var header = new byte[Math.Min(entry.Length, 2048)];
                stream.ReadExactly(header);
                var supported = HasArm64(header);
                result.Add(
                    new(item.Path, entry.FullName, supported)
                );
                if (!supported)
                    throw new InvalidDataException("原生库缺少 arm64 Mach-O：" + entry.FullName);
            }
        }

        if (!result.Any(
            x => x.Applicable && x.HasArm64
        ))
            throw new InvalidDataException("没有找到可验证的 macOS ARM64 原生库。");
        return result;
    }
}
