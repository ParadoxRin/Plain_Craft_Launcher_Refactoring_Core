using System.Buffers.Binary;

namespace Pcl.Core;
// [实现] 数据约定：InstanceIconSize 汇集 Width、Height、Format，供该模块传递状态。
public sealed record InstanceIconSize(int Width, int Height, string Format);
/// <summary>Passive image data only. Desktop decodes/re-encodes imported pixels before SavePng.</summary>
public sealed class InstanceIconService(AppPaths paths)
{
    public const string FileName = ".pcl-instance-icon.png";
    public const int MaximumBytes = 4 * 1024 * 1024;
    // [实现] InspectInput 的实现：按 bytes.Length is < 24 or > MaximumBytes 分支处理；执行 InstanceIconSize Validate(int width, int height, string kind) => width is> 0 and <= 2048 && hei…；按 bytes.AsSpan(0, 8) .SequenceEqual( new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 } ) && bytes.As… 分支处理。
    public static InstanceIconSize InspectInput(byte[] bytes)
    {
        if (bytes.Length is < 24 or > MaximumBytes)
            throw new InvalidDataException("图标需要 PNG/JPEG，文件不超过 4 MiB。");
        // [实现] Validate 的实现：计算并返回 width is> 0 and <= 2048 && height is> 0 and <= 2048 ? new(width, height, kind) : throw new Inva…。
        InstanceIconSize Validate(int width, int height, string kind) => width is> 0 and <= 2048 && height is> 0 and <= 2048
            ? new(width, height, kind)
            : throw new InvalidDataException("图标宽高应为 1–2048 像素。");
        if (bytes.AsSpan(0, 8)
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
            ) && bytes.AsSpan(12, 4)
            .SequenceEqual("IHDR"u8))
            return Validate(
                BinaryPrimitives.ReadInt32BigEndian(
                    bytes.AsSpan(16, 4)
                ),
                BinaryPrimitives.ReadInt32BigEndian(
                    bytes.AsSpan(20, 4)
                ),
                "png"
            );
        if (bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            // [实现] 条件成立（bytes[0] == 0xff && bytes[1] == 0xd8）时：准备 var at = 2；在 at < bytes.Length 条件下循环。
            var at = 2;
            while (at < bytes.Length)
            {
                // [实现] 循环满足 at < bytes.Length 时：按 bytes[at++] != 0xff 分支处理；在 at < bytes.Length && bytes[at] == 0xff 条件下循环；按 at >= bytes.Length 分支处理。
                if (bytes[at++] != 0xff)
                    throw new InvalidDataException("JPEG 图标结构无效。");
                while (at < bytes.Length && bytes[at] == 0xff)
                    at++;
                if (at >= bytes.Length)
                    break;
                var marker = bytes[at++];
                if (marker is 0xd9 or 0xda)
                    break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7)
                    continue;
                if (at + 2 > bytes.Length)
                    break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(
                    bytes.AsSpan(at, 2)
                );
                if (length < 2 || at + length > bytes.Length)
                    throw new InvalidDataException("JPEG 图标段长度无效。");
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    // [实现] 条件成立（marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd …）时：按 length < 8 分支处理；返回 Validate( BinaryPrimitives.ReadUInt16BigEndian( bytes.AsSpan(at + 5, 2) ), BinaryPrimitives.Rea…。
                    if (length < 8)
                        throw new InvalidDataException("JPEG 图标缺少尺寸。");
                    return Validate(
                        BinaryPrimitives.ReadUInt16BigEndian(
                            bytes.AsSpan(at + 5, 2)
                        ),
                        BinaryPrimitives.ReadUInt16BigEndian(
                            bytes.AsSpan(at + 3, 2)
                        ),
                        "jpeg"
                    );
                }

                at += length;
            }
        }

        throw new InvalidDataException("请选择有效 PNG 或 JPEG 图标。");
    }

    // [实现] Managed 的实现：准备 var directory = Path.GetFullPath(profile.GameDirectory)；准备 var root = Path.GetFullPath(paths.Game)；按 directory != root 分支处理。
    private string Managed(GameProfile profile)
    {
        var directory = Path.GetFullPath(profile.GameDirectory);
        var root = Path.GetFullPath(paths.Game);
        if (directory != root)
        {
            // [实现] 条件成立（directory != root）时：准备 var relative = Path.GetRelativePath( Path.Combine(paths.Root, "instances"), directory )；执行 _ = ResourceInstaller.SafeTarget( Path.Combine(paths.Root, "instances"), relative )。
            var relative = Path.GetRelativePath(
                Path.Combine(paths.Root, "instances"),
                directory
            );
            _ = ResourceInstaller.SafeTarget(
                Path.Combine(paths.Root, "instances"),
                relative
            );
        }

        return ResourceInstaller.SafeTarget(directory, FileName);
    }

    // [实现] SavePng 的实现：按 InspectInput(bytes) .Format != "png" 分支处理；准备 var target = Managed(profile)；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )。
    public void SavePng(GameProfile profile, byte[] bytes)
    {
        if (InspectInput(bytes)
            .Format != "png")
            throw new InvalidDataException("保存图标前需解码并转换 PNG。");
        var target = Managed(profile);
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!
        );
        var temporary = target + "." + Guid.NewGuid()
            .ToString("N") + ".partial";
        try
        {
            // [实现] 在异常保护范围内：执行 File.WriteAllBytes(temporary, bytes)；执行 File.Move(temporary, target, true)。
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, true);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(temporary) 分支处理。
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // [实现] Remove 的实现：准备 var path = Managed(profile)；按 File.Exists(path) 分支处理。
    public void Remove(GameProfile profile)
    {
        var path = Managed(profile);
        if (File.Exists(path))
            File.Delete(path);
    }

    // [实现] Read 的实现：准备 var path = ResourceInstaller.SafeTarget(gameDirectory, FileName)；按 !File.Exists(path) 分支处理；按 new FileInfo(path).Length > MaximumBytes 分支处理。
    public static byte[]? Read(string gameDirectory)
    {
        var path = ResourceInstaller.SafeTarget(gameDirectory, FileName);
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException("实例图标超过 4 MiB。");
        var bytes = File.ReadAllBytes(path);
        if (InspectInput(bytes)
            .Format != "png")
            throw new InvalidDataException("实例图标格式无效。");
        return bytes;
    }
}
