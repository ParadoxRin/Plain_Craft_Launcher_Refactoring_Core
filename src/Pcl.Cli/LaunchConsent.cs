namespace Pcl.Cli;
internal static class LaunchConsent
{
    // CLI owns text interaction only; Host/core decides the same guidance policy as Desktop.
    internal static async Task<Pcl.Core.Contracts.Launch.LaunchGuidanceConsent?> ConfirmGuidanceAsync(Pcl.Core.Contracts.Launch.LaunchGuidance guide, string culture, CancellationToken ct)
    {
        Console.WriteLine("PCL 原作者：龙腾猫跃。请考虑自愿支持原作者：" + guide.SponsorUrl);
        if (guide.ShowPurchase)
        {
            Console.WriteLine("请购买正版 Minecraft 支持开发者：" + guide.PurchaseUrl);
        }

        Console.WriteLine(
            guide.RequireDemo
                ? "尚未验证正版。输入 demo 启动官方试玩，其他输入取消。"
                : "阅读提示后输入 continue 继续、demo 试玩，其他输入取消。"
        );
        var input = await Console.In.ReadLineAsync(ct);
        var demo = input == "demo";
        if (!demo && (guide.RequireDemo || input != "continue"))
        {
            return null;
        }

        return new(culture, true, guide.ShowPurchase, demo);
    }
}
