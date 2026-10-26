using System.Buffers.Binary;
using System.Text;

namespace Pcl.Domain;
// 所有皮肤入口共用同一字节与尺寸校验，不依赖认证实现。
public static class SkinImagePolicy
{
    public static void ValidatePng(byte[] bytes, string kind)
    {
        if (bytes.Length is < 33 or > 4 * 1024 * 1024 || !bytes.AsSpan(0, 8)
            .SequenceEqual(
                new byte[]
                {
                    137,
                    80,
                    78,
                    71,
                    13,
                    10,
                    26,
                    10
                }
            ) || Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR")
            throw new InvalidDataException("请选择有效的 PNG 文件（不超过 4 MiB）。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(16, 4)
        );
        var height = BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(20, 4)
        );
        var valid = width is> 0 and <= 4096 && height is> 0 and <= 4096 && (kind == "skin"
            ? width % 64 == 0 && (height == width || height * 2 == width)
            : width % 64 == 0 && height * 2 == width);
        if (!valid)
            throw new InvalidDataException("皮肤需为 64×64 或 64×32 的整数倍；披风需为 64×32 的整数倍。");
    }
}
