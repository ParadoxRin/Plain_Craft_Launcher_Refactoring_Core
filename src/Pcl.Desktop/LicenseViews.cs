using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Core.Contracts;
using Pcl.Core.Contracts.Launch;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // The upstream author and their sponsorship link precede every other About item.
    internal StackPanel BuildAboutContent()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "PCL 原作者：" + ProductIdentity.UpstreamAuthor,
                FontSize = 22,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        );
        void Link(string name, string url)
        {
            var button = new Button
            {
                Content = name,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            button.Click += (_, _) => Open(url);
            panel.Children.Add(button);
        }

        Link("赞助 PCL 原作者 · 龙腾猫跃", ProductIdentity.SponsorUrl);
        panel.Children.Add(
            new TextBlock
            {
                Text = ProductInfo.Name + "\n版本 " + ProductInfo.Version,
                FontSize = 21,
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = ProductIdentity.ThirdPartyNotice,
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = ProductIdentity.SourceStatus,
                TextWrapping = TextWrapping.Wrap
            }
        );
        Link("本项目源码仓库（开发阶段私有）", ProductIdentity.SourceUrl);
        Link("PCL 上游源码", "https://github.com/Meloong-Git/PCL");
        Link(
            "PCL 存储库合理使用指南（随包原文）",
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Legal", "PCL-LICENCE.txt")
        );
        Link("PCL 指南上游原文", ProductIdentity.LicenseUrl);
        panel.Children.Add(
            new TextBlock
            {
                Text = "继续使用 PCL-LICENCE.txt 作为指南；第三方组件各自保留原许可，不将本项目改标为 CC BY-SA。\nWindows 运行时主色固定为蓝色，不提供赞助解锁或类似的主题表现。\n当前可用界面为 Avalonia；macOS 的目标界面为 SwiftUI，Windows/Linux 使用各自平台原生 UI。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        Link(
            "第三方许可与鸣谢",
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Legal", "THIRD-PARTY-NOTICES.md")
        );
        return panel;
    }

    // This UI is Chinese; Host determines required guidance and enforces the response.
    private async Task<LaunchGuidanceConsent?> ConfirmLaunchGuidanceAsync(string accountId, CancellationToken ct)
    {
        const string culture = "zh-CN";
        var guide = await service.Client.Call<LaunchGuidance>(
            "game.guidance",
            new LaunchGuidanceRequest(accountId, culture),
            ct
        );
        var window = new LaunchGuidanceWindow(guide, culture, Open);
        using var registration = ct.Register(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(window.Close)
        );
        return await window.PresentAsync<LaunchGuidanceConsent?>(this);
    }
}

/// <summary>Explicit read-and-continue guidance; sponsorship never unlocks a feature.</summary>
internal sealed class LaunchGuidanceWindow : WorkspaceWindow
{
    internal LaunchGuidanceWindow(LaunchGuidance guide, string culture, Action<string> open)
    {
        Title = "启动前提示 · " + ProductInfo.Name;
        Width = 640;
        Height = guide.ShowPurchase
            ? 520
            : 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "支持 PCL 原作者",
                FontSize = 22,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "PCL 由龙腾猫跃及贡献者持续开发。如果 PCL 对你有帮助，请考虑赞助原作者，支持免费维护与更新。赞助完全自愿，不解锁任何功能。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var sponsor = new Button
        {
            Content = "支持龙腾猫跃 / PCL",
            Name = "SponsorLink"
        };
        sponsor.Click += (_, _) => open(guide.SponsorUrl);
        panel.Children.Add(sponsor);
        if (guide.ShowPurchase)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = "支持正版 Minecraft\n如果你喜欢 Minecraft，请购买正版支持开发者。离线身份不代表已购买游戏，也不能代替微软官方验证。" + (guide.RequireDemo
                        ? "\n当前尚未完成正版验证，本次仅可选择官方试玩或取消。"
                        : ""),
                    TextWrapping = TextWrapping.Wrap
                }
            );
            var buy = new Button
            {
                Content = "购买正版 Minecraft",
                Name = "PurchaseLink"
            };
            buy.Click += (_, _) => open(guide.PurchaseUrl);
            panel.Children.Add(buy);
        }

        if (!guide.RequireDemo)
        {
            var proceed = new Button
            {
                Content = "已阅读，继续启动",
                Name = "GuidanceContinue"
            };
            proceed.Click += (_, _) => Close(
                new LaunchGuidanceConsent(culture, true, guide.ShowPurchase)
            );
            panel.Children.Add(proceed);
        }

        if (guide.ShowPurchase)
        {
            var demo = new Button
            {
                Content = "启动官方试玩",
                Name = "GuidanceDemo"
            };
            demo.Click += (_, _) => Close(
                new LaunchGuidanceConsent(culture, true, true, true)
            );
            panel.Children.Add(demo);
        }

        var cancel = new Button
        {
            Content = "取消启动",
            Name = "GuidanceCancel"
        };
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(cancel);
        Content = new ScrollViewer
        {
            Content = panel
        };
    }
}
