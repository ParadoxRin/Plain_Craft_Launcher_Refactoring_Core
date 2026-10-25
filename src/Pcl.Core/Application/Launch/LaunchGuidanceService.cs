using Pcl.Core.Contracts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Ports.Operations;
using Pcl.Domain;

namespace Pcl.Core.Application.Launch;
/// <summary>Host-owned guidance state stores only an anonymous verified-before flag.</summary>
public sealed class LaunchGuidanceService(AppPaths paths, AccountManager accounts)
{
    public LaunchGuidance Get(string accountId, string culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || culture.Length > 40)
        {
            throw new OperationInstallException("GUIDANCE_CULTURE_INVALID");
        }
        _ = System.Globalization.CultureInfo.GetCultureInfo(culture);
        var offline = accountId == "trial" || accounts.Accounts.SingleOrDefault(x => x.Id == accountId)?.Kind == AccountKind.Offline;
        if (accountId != "trial" && !accounts.Accounts.Any(x => x.Id == accountId))
        {
            throw new LoginRequiredException("账户不存在");
        }
        var file = Path.Combine(paths.Root, "launch-guidance.json");
        var verified = File.Exists(file) && (bool?)JsonFiles.Read(file)["verifiedBefore"] == true;
        var rule = LaunchGuidancePolicy.Evaluate(offline, verified, culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        return new(true, rule.Purchase, rule.DemoRequired, ProductIdentity.SponsorUrl, ProductIdentity.PurchaseUrl);
    }

    // Validate at the execution boundary before Java preparation or process creation.
    public bool Require(string accountId, string culture, bool sponsorRead, bool purchaseRead, bool demo)
    {
        var rule = Get(accountId, culture);
        if (!LaunchGuidancePolicy.Acknowledged(rule.ShowPurchase, rule.RequireDemo, sponsorRead, purchaseRead, demo))
        {
            throw new OperationInstallException("LAUNCH_GUIDANCE_REQUIRED");
        }
        return demo || accountId == "trial";
    }

    public static void RecordVerified(AppPaths paths, GameSession session)
    {
        if (session.UserType != "msa" || session.Ownership is null)
        {
            return;
        }
        GameLaunchAuthorization.Require(session);
        JsonFiles.Write(Path.Combine(paths.Root, "launch-guidance.json"), new { verifiedBefore = true });
    }
}
