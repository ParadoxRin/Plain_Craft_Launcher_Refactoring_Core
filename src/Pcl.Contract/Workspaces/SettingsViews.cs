using System.Text.Json;

namespace Pcl.Core.Contracts.Workspaces;
// 外观字段是展示草稿；所有业务验证和持久化由 Host 完成。
public sealed record AppearanceSettings
{
    public string Theme { get; init; } = "system";
    public string Accent { get; init; } = "#5D80F5";
    public string Title { get; init; } = Pcl.Core.Contracts.ProductIdentity.Name;
    public string BackgroundDirectory { get; init; } = "";
    public string Background { get; init; } = "";
    public double BackgroundOpacity { get; init; } = .15;
    public string Music { get; init; } = "";
    public double MusicVolume { get; init; } = .5;
    public bool PauseMusicInGame { get; init; } = true;
    public bool FirstLaunchChinese { get; init; } = true;
    public bool HideLauncherInGame { get; init; }
    public string HomeDocument { get; init; } = "";
    public double WindowOpacity { get; init; } = 1;
    public double BackgroundBlur { get; init; }
    public string BackgroundFit { get; init; } = "fill";
    public string TitleMode { get; init; } = "default";
    public bool MusicAutoPlay { get; init; } = true;
    public bool MinimizeLauncherInGame { get; init; }
    public string MusicDirectory { get; init; } = "";
    public bool MusicShuffle { get; init; }
    public string MusicMode { get; init; } = "normal";
    public string TitleImage { get; init; } = "";
    public bool BackgroundTint { get; init; }
    public string AccentMode { get; init; } = "static";
    public bool ShowStartupLogo { get; init; }
    public double AnimationSpeed { get; init; } = 1;
    public string HomeMode { get; init; } = "document";
    public string HomePreset { get; init; } = "welcome";
    public string LauncherVisibility { get; init; } = "legacy";
    public string[] HiddenRoutes { get; init; } = [];
    public string[] HiddenNavigation { get; init; } = [];
}

public sealed record LaunchHookSettings(
    bool Enabled = false,
    string Executable = "",
    string Arguments = "",
    bool Wait = true,
    int TimeoutSeconds = 60
);
public sealed record SettingsSnapshot(
    AppearanceSettings Appearance,
    JsonElement Launch,
    JsonElement Network,
    LaunchHookSettings Hook,
    HomePresetView[] Presets
);
public sealed record HomePresetView(string Id, string Name);
