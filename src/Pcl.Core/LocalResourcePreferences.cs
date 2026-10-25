using System.Text.RegularExpressions;

namespace Pcl.Core;
/// <summary>资源命名只改变受管理文件名，保留扩展名与清单身份；导入的用户文件不自动重命名。</summary>
public static class LocalResourcePreferences
{
    // [实现] FileName 的实现：执行 ResourceInstaller.ValidateFileName(original)；准备 var extension = Path.GetExtension(original)；准备 var stem = Path.GetFileNameWithoutExtension(original)。
    public static string FileName(string original, string name, string style)
    {
        ResourceInstaller.ValidateFileName(original);
        var extension = Path.GetExtension(original);
        var stem = Path.GetFileNameWithoutExtension(original);
        var safe = Regex.Replace(name, @"[\p{Cc}/\\:*?<>|\""\[\]【】]", "_")
            .Trim()
            .Trim('.');
        if (safe.Length > 60)
        {
            // [实现] 条件成立（safe.Length > 60）时：执行 safe = safe[..60]。
            safe = safe[..60];
        }

        if (safe.Length == 0 || style == "original")
        {
            // [实现] 条件成立（safe.Length == 0 || style == "original"）时：返回 original。
            return original;
        }

        var result = style switch
        {
            "brackets" => $"【{safe}】{stem}",
            "square" => $"[{safe}] {stem}",
            "prefix" => $"{safe}-{stem}",
            "suffix" => $"{stem}-{safe}",
            _ => throw new InvalidDataException("未知文件命名格式。")
        };
        if (System.Text.Encoding.UTF8.GetByteCount(result + extension) > 240)
        {
            // [实现] 条件成立（System.Text.Encoding.UTF8.GetByteCount(result + extension) > 240）时：抛出 new InvalidDataException("资源名称过长，请选择原文件名格式。")。
            throw new InvalidDataException("资源名称过长，请选择原文件名格式。");
        }

        ResourceInstaller.ValidateFileName(result + extension);
        return result + extension;
    }

    // [实现] Display 的实现：计算并返回 style == "file" ? $"{item.FileName}\n{item.Name} · {item.Version}" : $"{item.Name} · {item.Vers…。
    public static string Display(ManagedResource item, string style) => style == "file"
        ? $"{item.FileName}\n{item.Name} · {item.Version}"
        : $"{item.Name} · {item.Version}\n{item.FileName}";
}
