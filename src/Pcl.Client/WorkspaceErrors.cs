using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;
// 展示稳定的本地化错误文案，不将 Host 原始异常或请求中的密钥带回界面。
public static class WorkspaceErrors
{
    public static string Describe(Exception error) => error is OperationRejectedException failure
        ? failure.Code switch
        {
            "SETTINGS_CHANGED_RELOAD" => "设置已被另一个窗口修改，请重新打开设置页后重试。",
            "SEARCH_SESSION_EXPIRED" => "搜索会话已过期或 Host 已重启，请重新搜索。",
            "SEARCH_PAGE_LIMIT" => "已达到本次搜索的分页上限，请缩小关键词范围。",
            "CURSEFORGE_KEY_REQUIRED" => "需要自己的 CurseForge API Key；也可使用整合包本地导入。",
            "SECRET_REENTRY_REQUIRED" => "临时凭据已过期或已使用，请重新填写后重试。",
            "ACCOUNT_NOT_FOUND" => "账户已移除，请重新选择账户。",
            "LOGIN_CHALLENGE_EXPIRED" => "此次外置登录已过期或 Host 已重启，请重新登录。",
            "LOGIN_CHALLENGE_LIMIT" => "未完成的外置登录过多，请稍后重试。",
            "PUBLIC_SKIN_NOT_SAVED" => "请先获取并保存正版玩家皮肤，再刷新。",
            "WORKSPACE_SELECTION_INVALID" => "所选内容已失效或不属于当前账户，请重新获取。",
            "OFFLINE_ACCOUNT_REQUIRED" => "此操作需要选择离线账户。",
            "MICROSOFT_ACCOUNT_REQUIRED" => "此操作需要选择微软账户。",
            "EXTERNAL_ACCOUNT_REQUIRED" => "此操作需要选择外置账户。",
            "IMAGE_TOO_LARGE" => "图片超过允许大小，请选择较小的 PNG 文件。",
            "INVALID_DATA" => "数据未通过校验，请检查设置值、文件类型与版本兼容性。",
            "NETWORK_ERROR" => "网络请求失败，请检查网络后重试。",
            _ => failure.Code
        }
        : error.Message;
}
