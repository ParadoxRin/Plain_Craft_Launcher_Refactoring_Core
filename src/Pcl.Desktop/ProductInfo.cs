namespace Pcl.Desktop;
// UI 版本统一取可执行程序集，避免页脚、关于页面和发布包各写一个过期常量。
internal static class ProductInfo
{
    internal const string Name = Pcl.Core.Contracts.ProductIdentity.Name;
    internal static string Version => typeof(App).Assembly.GetName()
        .Version?.ToString(3) ?? "0.38.0";
}
