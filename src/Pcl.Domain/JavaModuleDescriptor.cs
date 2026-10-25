using System.Buffers.Binary;
using System.Text;

namespace Pcl.Domain;
/// <summary>从class文件字节读取Module属性，不加载类或执行JAR。输入截断、错误常量类型及长度越界均拒绝。</summary>
public static class JavaModuleDescriptor
{
    public static string ReadName(byte[] bytes)
    {
        var reader = new Reader(bytes);
        if (reader.U4() != 0xCAFEBABE)
        {
            throw new InvalidDataException("无效的module-info.class文件头。");
        }

        reader.U2();
        reader.U2();
        var count = reader.U2();
        var tags = new byte[count];
        var text = new string? [count];
        var references = new ushort[count];
        for (var i = 1; i < count; i++)
        {
            tags[i] = reader.U1();
            switch (tags[i])
            {
                case 1:
                    text[i] = ModifiedUtf8(
                        reader.Bytes(
                            reader.U2()
                        )
                    );
                    break;
                case 7:
                case 8:
                case 16:
                case 19:
                case 20:
                    references[i] = reader.U2();
                    break;
                case 3:
                case 4:
                case 9:
                case 10:
                case 11:
                case 12:
                case 17:
                case 18:
                    reader.Bytes(4);
                    break;
                case 5:
                case 6:
                    reader.Bytes(8);
                    i++;
                    break;
                case 15:
                    reader.Bytes(3);
                    break;
                default:
                    throw new InvalidDataException("未知class常量池类型。");
            }
        }

        string Utf(ushort index)
        {
            if (index == 0 || index >= count || tags[index] != 1 || text[index] is null)
            {
                throw new InvalidDataException("class字符串索引无效。");
            }

            return text[index]!;
        }

        var access = reader.U2();
        var thisClass = reader.U2();
        if ((access & 0x8000) == 0 || thisClass == 0 || thisClass >= count || tags[thisClass] != 7 || Utf(references[thisClass]) != "module-info")
        {
            throw new InvalidDataException("文件不是模块描述符。");
        }

        if (reader.U2() != 0 || reader.U2() != 0 || reader.U2() != 0 || reader.U2() != 0)
        {
            throw new InvalidDataException("模块描述符不能声明父类、接口、字段或方法。");
        }

        var attributes = reader.U2();
        string? name = null;
        for (var i = 0; i < attributes; i++)
        {
            var attributeName = Utf(
                reader.U2()
            );
            var length = reader.U4();
            if (length > int.MaxValue)
            {
                throw new InvalidDataException("class属性过大。");
            }

            var data = reader.Bytes((int)length);
            if (attributeName == "Module")
            {
                if (name is not null || data.Length < 16)
                {
                    throw new InvalidDataException("Module属性重复或截断。");
                }

                var index = BinaryPrimitives.ReadUInt16BigEndian(data);
                if (index == 0 || index >= count || tags[index] != 19)
                {
                    throw new InvalidDataException("Module名称索引无效。");
                }

                name = Utf(references[index]);
            }
        }

        if (!reader.End || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("Module属性缺失或class包含多余数据。");
        }

        return name;
    }

    /// <summary>class常量使用修改版UTF-8；四字节码点以两个UTF-16代理项编码，不能用普通UTF-8替代。</summary>
    private static string ModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            var first = bytes[i];
            if (first is >= 1 and <= 127)
            {
                result.Append((char)first);
            }
            else if ((first & 0xe0) == 0xc0 && i + 1 < bytes.Length && (bytes[i + 1] & 0xc0) == 0x80)
            {
                var value = ((first & 0x1f) << 6) | (bytes[++i] & 0x3f);
                if (value != 0 && value < 128)
                {
                    throw new InvalidDataException("class字符串存在过长编码。");
                }

                result.Append((char)value);
            }
            else if ((first & 0xf0) == 0xe0 && i + 2 < bytes.Length && (bytes[i + 1] & 0xc0) == 0x80 && (bytes[i + 2] & 0xc0) == 0x80)
            {
                var value = ((first & 15) << 12) | ((bytes[++i] & 63) << 6) | (bytes[++i] & 63);
                if (value < 2048)
                {
                    throw new InvalidDataException("class字符串存在过长编码。");
                }

                result.Append((char)value);
            }
            else
            {
                throw new InvalidDataException("class修改版UTF-8编码无效。");
            }
        }

        return result.ToString();
    }

    /// <summary>所有读取共用边界检查，长度由输入声明也不能越过缓冲区。</summary>
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> remaining = bytes;
        public bool End => remaining.IsEmpty;

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > remaining.Length)
            {
                throw new InvalidDataException("class文件已截断。");
            }

            var result = remaining[..count];
            remaining = remaining[count..];
            return result;
        }

        public byte U1() => Bytes(1)[0];
        public ushort U2() => BinaryPrimitives.ReadUInt16BigEndian(
            Bytes(2)
        );
        public uint U4() => BinaryPrimitives.ReadUInt32BigEndian(
            Bytes(4)
        );
    }
}
