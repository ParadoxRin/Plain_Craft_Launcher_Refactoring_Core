namespace Pcl.Core;
// Created only after MicrosoftAuthentication has checked entitlements and the Java profile.
// Account metadata and caller-supplied "msa" strings cannot create this in-memory proof.
internal sealed record VerifiedGameOwnership(string Name, string Uuid, string AccessToken, DateTimeOffset ExpiresAt);
// 按账户类型验证启动身份：离线身份仅检查本地一致性，微软身份仍要求经过权益校验的内存凭据。
public static class GameLaunchAuthorization
{
    // [实现] Require 的实现：按 IsOfflineIdentity(session) 分支处理；准备 var proof = session.Ownership；按 session.Demo || session.UserType != "msa" || proof is null || proof.ExpiresAt <= DateTimeOffset… 分支处理。
    public static void Require(GameSession session)
    {
        // 离线身份不代表购买或微软验证；固定 UUID 与占位令牌沿用 PCL2 的离线算法。
        if (session == GameSession.Trial || IsOfflineIdentity(session))
        {
            // [实现] 条件成立（IsOfflineIdentity(session)）时：结束当前过程。
            return;
        }

        var proof = session.Ownership;
        if (session.Demo || session.UserType != "msa" || proof is null || proof.ExpiresAt <= DateTimeOffset.UtcNow || proof.Name != session.Name || proof.Uuid != session.Uuid || proof.AccessToken != session.AccessToken)
            throw new LoginRequiredException("请使用有效的离线身份，或重新登录拥有 Minecraft Java 版的微软账户。");
    }

    /// <summary>校验离线会话的字段一致性；不会为其创建微软正版证明。</summary>
    public static bool IsOfflineIdentity(GameSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Name) || session.Demo || session.UserType != "legacy" || session.Ownership is not null || session.Xuid is not null)
        {
            // [实现] 条件成立（string.IsNullOrWhiteSpace(session.Name) || session.Demo || session.UserType != "legacy" || sess…）时：返回 false。
            return false;
        }

        try
        {
            // [实现] 在异常保护范围内：准备 var expected = AccountManager.OfflineSession(session.Name)；返回 session.Name == expected.Name && session.Uuid == expected.Uuid && session.AccessToken == expect…。
            return new[]
            {
                "auto",
                "wide",
                "slim"
            }.Select(
                model => AccountManager.OfflineSession(session.Name, model)
            )
                .Any(
                    expected => session.Name == expected.Name && session.Uuid == expected.Uuid && session.AccessToken == expected.AccessToken
                );
        }
        catch (InvalidDataException)
        {
            // [实现] 捕获 (InvalidDataException) 后：返回 false。
            return false;
        }
    }
}
