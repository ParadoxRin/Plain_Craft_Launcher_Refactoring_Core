using System.Buffers.Binary;
using System.IO.Compression;

namespace Pcl.Core;
/// <summary>将旧 64×32 皮肤规范化为 64×64；镜像生成左腿与左臂，保留上半部所有原像素。</summary>
public static class SkinPixels
{
    public static byte[] Modern(byte[] png) => Convert(png, false);
    public static byte[] Legacy(byte[] png) => Convert(png, true);
    // 解码 PNG 扫描行过滤器，转换为 RGBA 后补齐或裁剪旧皮肤布局。
    private static byte[] Convert(byte[] png, bool legacy)
    {
        Pcl.Domain.SkinImagePolicy.ValidatePng(png, "skin");
        int w = (int)BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(16, 4)
        );
        int h = (int)BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(20, 4)
        );
        if (w != 64 || h is not (32 or 64))
        {
            throw new InvalidDataException("离线皮肤需要 64×32 或 64×64 PNG。");
        }

        if (h == (legacy
            ? 32
            : 64))
        {
            return png;
        }

        if (png[24] != 8 || png[28] != 0)
        {
            throw new InvalidDataException("旧皮肤需使用非交错 8 位 PNG。");
        }

        int type = png[25], channels = type == 6
            ? 4
            : type == 2
                ? 3
                : type == 3
                    ? 1
                    : throw new InvalidDataException("不支持此旧 PNG 色彩格式。");
        byte[] palette = [], alpha = [];
        using var packed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(offset, 4)
            ));
            if (length < 0 || offset + 12L + length > png.Length)
            {
                throw new InvalidDataException("PNG 数据损坏。");
            }

            var name = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (name == "IDAT")
            {
                packed.Write(png, offset + 8, length);
            }

            if (name == "PLTE")
            {
                palette = png.AsSpan(offset + 8, length)
                    .ToArray();
            }

            if (name == "tRNS")
            {
                alpha = png.AsSpan(offset + 8, length)
                    .ToArray();
            }

            offset += length + 12;
        }

        packed.Position = 0;
        using var z = new ZLibStream(packed, CompressionMode.Decompress);
        var raw = new byte[h * (w * channels + 1)];
        z.ReadExactly(raw);
        var rgba = new byte[64 * 64 * 4];
        var previous = new byte[w * channels];
        for (int y = 0; y < h; y++)
        {
            int offset = y * (w * channels + 1), filter = raw[offset];
            var row = raw.AsSpan(offset + 1, w * channels)
                .ToArray();
            for (int x = 0; x < row.Length; x++)
            {
                int a = x < channels
                    ? 0
                    : row[x - channels], b = previous[x], c = x < channels
                    ? 0
                    : previous[x - channels];
                int value = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("PNG 过滤器无效。")
                };
                row[x] = unchecked((byte)(row[x] + value));
            }

            for (int x = 0; x < w; x++)
            {
                int target = (y * 64 + x) * 4, source = x * channels;
                if (type == 3)
                {
                    int index = row[source];
                    if (index * 3 + 2 >= palette.Length)
                    {
                        throw new InvalidDataException("PNG 调色板无效。");
                    }

                    palette.AsSpan(index * 3, 3)
                        .CopyTo(
                            rgba.AsSpan(target)
                        );
                    rgba[target + 3] = index < alpha.Length
                        ? alpha[index]
                        : (byte)255;
                }
                else
                {
                    row.AsSpan(source, 3)
                        .CopyTo(
                            rgba.AsSpan(target)
                        );
                    rgba[target + 3] = type == 6
                        ? row[source + 3]
                        : (byte)255;
                }
            }

            previous = row;
        }

        if (legacy)
        {
            return Encode(rgba, 32);
        }

        // 与经典皮肤布局一致，目标矩形横向反转，生成原图没有的左侧肢体。
        foreach (var r in new[]
        {
            (24, 48, 20, 52, 4, 16, 8, 20),
            (28, 48, 24, 52, 8, 16, 12, 20),
            (20, 52, 16, 64, 8, 20, 12, 32),
            (24, 52, 20, 64, 4, 20, 8, 32),
            (28, 52, 24, 64, 0, 20, 4, 32),
            (32, 52, 28, 64, 12, 20, 16, 32),
            (40, 48, 36, 52, 44, 16, 48, 20),
            (44, 48, 40, 52, 48, 16, 52, 20),
            (36, 52, 32, 64, 48, 20, 52, 32),
            (40, 52, 36, 64, 44, 20, 48, 32),
            (44, 52, 40, 64, 40, 20, 44, 32),
            (48, 52, 44, 64, 52, 20, 56, 32)
        }

        )
        {
            for (int y = 0; y < r.Item8 - r.Item6; y++)
            {
                for (int x = 0; x < r.Item7 - r.Item5; x++)
                {
                    rgba.AsSpan(((r.Item6 + y) * 64 + r.Item5 + x) * 4, 4)
                        .CopyTo(
                            rgba.AsSpan(((r.Item2 + y) * 64 + r.Item1 - 1 - x) * 4, 4)
                        );
                }
            }
        }

        return Encode(rgba);
    }

    // 还原 PNG Paeth 过滤器选择的邻近像素预测值。
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc
            ? a
            : pb <= pc
                ? b
                : c;
    }

    // 使用无行过滤的 RGBA 和 zlib 编码，生成指定高度的 PNG。
    private static byte[] Encode(byte[] pixels, int height = 64)
    {
        using var output = new MemoryStream();
        output.Write(
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
        );
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(0, 4),
            64
        );
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(4, 4),
            (uint)height
        );
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);
        using var data = new MemoryStream();
        using (var zip = new ZLibStream(data, CompressionLevel.Optimal, true))
        {
            for (int y = 0; y < height; y++)
            {
                zip.WriteByte(0);
                zip.Write(pixels, y * 256, 256);
            }
        }

        Chunk(
            output,
            "IDAT",
            data.ToArray()
        );
        Chunk(
            output,
            "IEND",
            []
        );
        return output.ToArray();
    }

    // 写入标准 PNG 区块长度、数据和 CRC，供游戏直接解码。
    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        var bytes = System.Text.Encoding.ASCII.GetBytes(type)
            .Concat(data)
            .ToArray();
        output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0
                    ? 0xedb88320u
                    : 0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xffffffff);
        output.Write(number);
    }
}
