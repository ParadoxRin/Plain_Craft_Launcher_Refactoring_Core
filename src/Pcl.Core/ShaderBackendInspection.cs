namespace Pcl.Core;
/// <summary>仅陈述已扫描到的后端版本和当前加载器，不据此承诺平台兼容或自动卸载环境。</summary>
public static class ShaderBackendInspection
{
    public static string[] Describe(string gameDirectory, string loader, string gameVersion) => new LocalResourceService(gameDirectory, loader, gameVersion).Scan()
        .Where(
            x => x.Kind == ResourceKind.Mod && x.Error is null && x.ModIds.Any(
                id => id is "iris" or "oculus"
            )
        )
        .Select(
            x => "检测到 " + string.Join(
                " / ",
                x.ModIds.Where(
                    id => id is "iris" or "oculus"
                )
            ) + " " + x.Version + "；Minecraft " + gameVersion + "，加载器 " + loader + (x.Enabled
                ? "，已启用。"
                : "，已禁用。") + "撤销应用只恢复配置，卸载光影包只处理受管理的包；环境组件保留，Mac 光影效果需实际验证。"
        )
        .ToArray();
}
