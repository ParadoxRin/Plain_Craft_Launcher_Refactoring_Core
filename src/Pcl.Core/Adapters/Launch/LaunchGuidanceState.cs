using Pcl.Core.Ports.Launch;

namespace Pcl.Core.Adapters.Launch;
// 仅落盘匿名正版验证标志，不保存账户身份或凭据。
public sealed class LaunchGuidanceState(AppPaths paths, AccountManager accounts) : ILaunchGuidanceState
{
    public LaunchAccountState Read(string accountId)
    {
        var account = accounts.Accounts.SingleOrDefault(
            x => x.Id == accountId
        );
        var file = Path.Combine(paths.Root, "launch-guidance.json");
        return new(
            accountId == "trial" || account is not null,
            accountId == "trial" || account?.Kind == AccountKind.Offline,
            File.Exists(file) && (bool? )JsonFiles.Read(file)["verifiedBefore"] == true
        );
    }

    public static void RecordVerified(AppPaths paths, GameSession session)
    {
        if (session.UserType != "msa" || session.Ownership is null)
        {
            return;
        }

        GameLaunchAuthorization.Require(session);
        JsonFiles.Write(
            Path.Combine(paths.Root, "launch-guidance.json"),
            new
            {
                verifiedBefore = true
            }
        );
    }
}
