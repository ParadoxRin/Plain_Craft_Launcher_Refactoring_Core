namespace Pcl.Core.Adapters.Appearance;
/// <summary>Normalize host-specific decoration before persistence and desktop projection.</summary>
public static class PlatformAppearance
{
    public static AppearanceSettings Normalize(AppearanceSettings value, bool windows)
    {
        return windows
            ? value with { Accent = "#0875D1", AccentMode = "static", TitleMode = "default", TitleImage = "" }
            : value;
    }
}
