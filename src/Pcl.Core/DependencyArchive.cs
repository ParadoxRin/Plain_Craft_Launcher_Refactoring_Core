namespace Pcl.Core;
// 本地资源管理：扫描模组、光影和资源包，在文件变更前后校验依赖并保留事务恢复信息。
internal static class DependencyArchive
{
    // Archive-internal paths are not instance override paths: a root-level jar is legal.
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) || path.Split('/')
            .Any(
                x => x is "" or "." or ".."
            ))
            throw new InvalidDataException("嵌套归档路径不安全：" + path);
    }
}
