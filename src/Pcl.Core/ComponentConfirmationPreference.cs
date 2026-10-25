namespace Pcl.Core;
/// <summary>只保存副本解释弹窗偏好；执行前校验始终由迁移服务与 Host 完成。</summary>
public static class ComponentConfirmationPreference
{
    private static string PathOf(string root) => ResourceInstaller.SafeTarget(root, "component-confirmation.json");
    public static bool Read(string root)
    {
        var path = PathOf(root);
        return File.Exists(path) && (bool? )JsonFiles.Read(path)["suppress"] == true;
    }

    public static void Save(string root, bool suppress) => JsonFiles.Write(
        PathOf(root),
        new
        {
            suppress
        }
    );
}
