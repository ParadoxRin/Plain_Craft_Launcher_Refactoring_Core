using Pcl.Core.Contracts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Ports.Operations;
using Pcl.Domain;

namespace Pcl.Core.Application.Launch;
/// <summary>Host-owned guidance state stores only an anonymous verified-before flag.</summary>
public sealed class LaunchGuidanceService(Pcl.Core.Ports.Launch.ILaunchGuidanceState state)
{
    public LaunchGuidance Get(string accountId, string culture)
    {
        if (string.IsNullOrWhiteSpace(culture) || culture.Length > 40)
        {
            throw new OperationInstallException("GUIDANCE_CULTURE_INVALID");
        }

        _ = System.Globalization.CultureInfo.GetCultureInfo(culture);
        var input = state.Read(accountId);
        if (!input.Exists)
        {
            throw new Pcl.Core.LoginRequiredException("账户不存在");
        }

        var rule = LaunchGuidancePolicy.Evaluate(
            input.Offline,
            input.VerifiedBefore,
            culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        );
        return new(
            true,
            rule.Purchase,
            rule.DemoRequired,
            ProductIdentity.SponsorUrl,
            ProductIdentity.PurchaseUrl
        );
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
}
