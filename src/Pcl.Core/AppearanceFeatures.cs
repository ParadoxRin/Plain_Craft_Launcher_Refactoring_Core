namespace Pcl.Core;
// 集中枚举隐藏目标和背景定位，供验证、设置页及实际控件消费，避免保存了不存在的键。
public static class AppearanceFeatures
{
    public static readonly string[] HiddenRoutes = [
        "launch",
        "download",
        "settings",
        "more",
        "setup-launch",
        "setup-link",
        "setup-ui",
        "setup-system",
        "help",
        "about",
        "feedback",
        "vote",
        "mod",
        "shader",
        "modpack",
        "datapack",
        "resourcepack",
        "email",
        "version-management",
        "mod-update",
        "hide-settings",
        "version-edit",
        "multiplayer"
    ];
    public static readonly string[] BackgroundFits = [
        "fill",
        "fit",
        "stretch",
        "center",
        "tile",
        "top-left",
        "left",
        "bottom-left",
        "top",
        "middle",
        "bottom",
        "top-right",
        "right",
        "bottom-right",
        "auto"
    ];
    // [实现] ShouldPlayMusic 的实现：计算并返回 value.MusicMode == "game" ? inGame : value.MusicAutoPlay && (!inGame || !value.PauseMusicInGame…。
    public static bool ShouldPlayMusic(AppearanceSettings value, bool inGame) => value.MusicMode == "game"
        ? inGame
        : value.MusicAutoPlay && (!inGame || !value.PauseMusicInGame);
    // 每次刷新重新扫描文件夹，并尽量避开当前图片；空文件夹回退单图。
    public static string SelectBackground(AppearanceSettings value, string? previous = null)
    {
        if (value.BackgroundDirectory.Length == 0)
        {
            // [实现] 条件成立（value.BackgroundDirectory.Length == 0）时：返回 value.Background。
            return value.Background;
        }

        if (!Directory.Exists(value.BackgroundDirectory))
        {
            // [实现] 条件成立（!Directory.Exists(value.BackgroundDirectory)）时：抛出 new DirectoryNotFoundException("背景文件夹不存在。")。
            throw new DirectoryNotFoundException("背景文件夹不存在。");
        }

        var files = Directory.EnumerateFiles(value.BackgroundDirectory)
            .Where(
                x => new[]
                {
                    ".png",
                    ".jpg",
                    ".jpeg",
                    ".bmp",
                    ".webp"
                }.Contains(
                    Path.GetExtension(x)
                        .ToLowerInvariant()
                )
            )
            .ToArray();
        var candidates = files.Where(
            x => x != previous
        )
            .ToArray();
        if (candidates.Length == 0)
        {
            // [实现] 条件成立（candidates.Length == 0）时：执行 candidates = files。
            candidates = files;
        }

        return candidates.Length == 0
            ? value.Background
            : candidates[Random.Shared.Next(candidates.Length)];
    }

    // [实现] MusicFiles 的实现：按 value.MusicDirectory.Length == 0 分支处理；按 !Directory.Exists(value.MusicDirectory) 分支处理；返回 Directory.EnumerateFiles(value.MusicDirectory) .Where( x => new[] { ".wav", ".mp3", ".m4a", ".a…。
    public static string[] MusicFiles(AppearanceSettings value)
    {
        if (value.MusicDirectory.Length == 0)
        {
            // [实现] 条件成立（value.MusicDirectory.Length == 0）时：返回 value.Music.Length > 0 ? [ value.Music ] : []。
            return value.Music.Length > 0
                ? [
                    value.Music
                ]
                : [];
        }

        if (!Directory.Exists(value.MusicDirectory))
        {
            // [实现] 条件成立（!Directory.Exists(value.MusicDirectory)）时：抛出 new DirectoryNotFoundException("音乐文件夹不存在。")。
            throw new DirectoryNotFoundException("音乐文件夹不存在。");
        }

        return Directory.EnumerateFiles(value.MusicDirectory)
            .Where(
                x => new[]
                {
                    ".wav",
                    ".mp3",
                    ".m4a",
                    ".aiff",
                    ".aac",
                    ".flac"
                }.Contains(
                    Path.GetExtension(x)
                        .ToLowerInvariant()
                )
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
