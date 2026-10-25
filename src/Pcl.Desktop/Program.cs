using Avalonia;
using System.Text;

namespace Pcl.Desktop;
// 应用入口与主界面：初始化服务和界面，将命令或用户事件路由到对应核心功能。
internal static class Program
{
    internal static string[] Arguments = [];
    // 初始化界面编码并启动 Avalonia；业务和 MeloongCore 的初始化由独立 Host 完成。
    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // [实现] BuildAvaloniaApp 的实现：计算并返回 AppBuilder.Configure<App>() .UsePlatformDetect() .LogToTrace()。
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
