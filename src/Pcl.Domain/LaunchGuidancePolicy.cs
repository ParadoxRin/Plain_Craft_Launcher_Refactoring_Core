namespace Pcl.Domain;
/// <summary>Pure upstream guidance policy. Notices are more frequent than the upstream count milestones.</summary>
public static class LaunchGuidancePolicy
{
    public static (bool Purchase, bool DemoRequired) Evaluate(bool offline, bool verifiedBefore, bool chineseUi)
    {
        var purchase = offline && !verifiedBefore;
        return (purchase, purchase && !chineseUi);
    }

    public static bool Acknowledged(bool purchase, bool demoRequired, bool sponsorRead, bool purchaseRead, bool demo)
    {
        return sponsorRead && (!purchase || purchaseRead) && (!demoRequired || demo);
    }
}
