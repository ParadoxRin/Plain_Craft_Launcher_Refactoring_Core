using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：AppearanceSettings 汇集 ，供该模块传递状态。
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

    // [实现] Validate 的实现：按 !double.IsFinite(WindowOpacity) || WindowOpacity is < .5 or > 1 || !double.IsFinite(BackgroundB… 分支处理；按 HiddenRoutes.Length > 32 || HiddenRoutes.Any( x => !AppearanceFeatures.HiddenRoutes.Contains(x)… 分支处理；按 MusicMode is not ("normal" or "game") || AccentMode is not ("static" or "rainbow" or "aurora") … 分支处理。
    public void Validate()
    {
        if (!double.IsFinite(WindowOpacity) || WindowOpacity is < .5 or > 1 || !double.IsFinite(BackgroundBlur) || BackgroundBlur is < 0 or > 40 || !AppearanceFeatures.BackgroundFits.Contains(BackgroundFit) || TitleMode is not ("default" or "none" or "text" or "image"))
        {
            // [实现] 条件成立（!double.IsFinite(WindowOpacity) || WindowOpacity is < .5 or > 1 || !double.IsFinite(BackgroundB…）时：抛出 new InvalidDataException("窗口或背景设置超出范围。")。
            throw new InvalidDataException("窗口或背景设置超出范围。");
        }

        if (HiddenRoutes.Length > 32 || HiddenRoutes.Any(
            x => !AppearanceFeatures.HiddenRoutes.Contains(x)
        ))
        {
            // [实现] 条件成立（HiddenRoutes.Length > 32 || HiddenRoutes.Any( x => !AppearanceFeatures.HiddenRoutes.Contains(x)…）时：抛出 new InvalidDataException("隐藏导航目标无效。")。
            throw new InvalidDataException("隐藏导航目标无效。");
        }

        if (MusicMode is not ("normal" or "game") || AccentMode is not ("static" or "rainbow" or "aurora") || !double.IsFinite(AnimationSpeed) || AnimationSpeed is < 0 or > 5 || HomeMode is not ("document" or "blank" or "preset") || (HomePreset is not ("welcome" or "shortcuts" or "tips") && !System.Text.RegularExpressions.Regex.IsMatch(HomePreset, "^local-[a-f0-9]{32}$")) || LauncherVisibility is not ("legacy" or "close" or "hide-close" or "hide-show" or "minimize" or "keep"))
        {
            // [实现] 条件成立（MusicMode is not ("normal" or "game") || AccentMode is not ("static" or "rainbow" or "aurora") …）时：抛出 new InvalidDataException("个性化模式无效。")。
            throw new InvalidDataException("个性化模式无效。");
        }

        if (Theme is not ("system" or "light" or "dark") || !System.Text.RegularExpressions.Regex.IsMatch(Accent, "^#[0-9a-fA-F]{6}$"))
            throw new InvalidDataException("主题或颜色无效。");
        if (Title.Length is < 1 or > 100 || Title.Any(char.IsControl) || BackgroundOpacity is < 0 or > 1 || MusicVolume is < 0 or > 1 || double.IsNaN(BackgroundOpacity) || double.IsNaN(MusicVolume))
            throw new InvalidDataException("外观设置超出范围。");
        foreach (var path in new[]
        {
            Background,
            BackgroundDirectory,
            Music,
            MusicDirectory,
            TitleImage
        }

        )
            if (path.Length > 0 && !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("图片与音乐需选择本地完整路径。");
        if (HomeDocument.Length > 0 && !Path.IsPathFullyQualified(HomeDocument) && !(Uri.TryCreate(HomeDocument, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0))
            throw new InvalidDataException("主页使用本地 JSON 或 HTTPS 地址。");
        if (HiddenNavigation.Length > 20 || HiddenNavigation.Any(
            x => !new[]
            {
                "NavMods",
                "NavShaders",
                "NavPacks",
                "NavDataPacks",
                "NavModpacks",
                "NavCurseForge",
                "NavManaged",
                "NavLocal",
                "NavPackTools"
            }.Contains(x)
        ))
            throw new InvalidDataException("隐藏项目无效。");
    }
}

// [实现] 数据约定：HomeLink 汇集 Text、Url、Kind，供该模块传递状态。
public sealed record HomeLink(string Text, string Url, string Kind = "url");
// [实现] 数据约定：HomeDocument 汇集 Title、Body、Links，供该模块传递状态。
public sealed record HomeDocument(string Title, string Body, HomeLink[] Links)
{
    public string? Markup { get; init; }

    // [实现] Validate 的实现：按 Title.Length > 100 || Body.Length > 100000 || Links.Length > 30 分支处理；遍历 Links 处理 item。
    public void Validate()
    {
        if (Title.Length > 100 || Body.Length > 100000 || Links.Length > 30 || Markup?.Length > 1_048_576)
            throw new InvalidDataException("主页内容超出范围。");
        foreach (var item in Links)
        {
            // [实现] 对 Links 中的每一项：按 item.Text.Length > 100 || item.Url.Length > 32768 || item.Text.Any(char.IsControl) 分支处理；按 item.Kind == "url" 分支处理。
            if (item.Text.Length > 100 || item.Url.Length > 32768 || item.Text.Any(char.IsControl))
                throw new InvalidDataException("主页操作内容超出范围。");
            if (item.Kind == "url")
            {
                // [实现] 条件成立（item.Kind == "url"）时：按 !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"… 分支处理。
                if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
                    throw new InvalidDataException("主页链接仅允许网页地址。");
            }
            else if (item.Kind == "navigate")
            {
                // [实现] 条件成立（item.Kind == "navigate"）时：按 !new[] { "instances", "downloads", "tasks", "settings", "resources", "packs", "network" }.Conta… 分支处理。
                if (!new[]
                {
                    "instances",
                    "downloads",
                    "tasks",
                    "settings",
                    "resources",
                    "packs",
                    "network"
                }.Contains(item.Url))
                    throw new InvalidDataException("主页导航目标无效。");
            }
            else if (item.Kind == "join_room")
            {
                // [实现] 条件成立（item.Kind == "join_room"）时：执行 _ = LinkRoom.Decode(item.Url)。
                _ = LinkRoom.Decode(item.Url);
            }
            else if (item.Kind is not ("copy" or "message" or "launch" or "refresh"))
                throw new InvalidDataException("主页不支持此操作，命令和执行设置只能在本地设置中配置。");
        }
    }
}

// 设置和个性化：读取、校验并应用用户配置，更新界面或处理本地文件入口，保存可恢复设置。
public sealed class AppearanceService(AppPaths paths)
{
    // [实现] Load 的实现：准备 var file = Path.Combine(paths.Root, "appearance.json")；准备 var value = File.Exists(file) ? JsonFiles.Read(file) .ToObject<AppearanceSettings>() ?? new() :…；执行 value.Validate()。
    public AppearanceSettings Load()
    {
        var file = Path.Combine(paths.Root, "appearance.json");
        var value = File.Exists(file)
            ? JsonFiles.Read(file)
                .ToObject<AppearanceSettings>() ?? new()
            : new();
        value = Adapters.Appearance.PlatformAppearance.Normalize(value, OperatingSystem.IsWindows());
        value.Validate();
        return value;
    }

    // [实现] Save 的实现：执行 value.Validate()；执行 JsonFiles.Write( Path.Combine(paths.Root, "appearance.json"), value )。
    public void Save(AppearanceSettings value)
    {
        value = Adapters.Appearance.PlatformAppearance.Normalize(value, OperatingSystem.IsWindows());
        value.Validate();
        JsonFiles.Write(
            Path.Combine(paths.Root, "appearance.json"),
            value
        );
    }

    // [实现] ReadHomeAsync 的实现：准备 var settings = Load()；按 settings.HomeMode == "blank" 分支处理；按 settings.HomeMode == "preset" 分支处理。
    public async Task<HomeDocument> ReadHomeAsync(CancellationToken ct = default)
    {
        var settings = Load();
        if (settings.HomeMode == "blank")
        {
            // [实现] 条件成立（settings.HomeMode == "blank"）时：返回 new( "", "", [] )。
            return new(
                "",
                "",
                []
            );
        }

        if (settings.HomeMode == "preset")
        {
            // [实现] 条件成立（settings.HomeMode == "preset"）时：返回 settings.HomePreset switch { "shortcuts" => new( "快捷操作", "常用功能直接从主页打开。", [ new("下载游戏", "downloa…。
            if (settings.HomePreset.StartsWith("local-", StringComparison.Ordinal))
            {
                return new HomePresets(paths).Read(settings.HomePreset);
            }

            return settings.HomePreset switch
            {
                "shortcuts" => new(
                    "快捷操作",
                    "常用功能直接从主页打开。",
                    [
                        new("下载游戏", "downloads", "navigate"),
                        new("实例管理", "instances", "navigate"),
                        new("任务与诊断", "tasks", "navigate")
                    ]
                ),
                "tips" => new(
                    "你知道吗？",
                    new[]
                    {
                        "升级世界前可在实例管理中备份存档。",
                        "模组必须与游戏版本及加载器一致；同名文件不代表兼容。",
                        "光影文件放在 shaderpacks；资源包放在 resourcepacks。",
                        "离线皮肤可以通过玩家名从官方获取，并保存后刷新。"
                    }[Random.Shared.Next(4)],
                    [
                        new("设置", "settings", "navigate")
                    ]
                ),
                _ => new(
                    "欢迎使用 PCL Mac",
                    "选择版本、下载资源并启动游戏。",
                    [
                        new("下载游戏", "downloads", "navigate")
                    ]
                )
            };
        }

        var location = settings.HomeDocument;
        if (location.Length == 0)
            return new(
                "欢迎",
                "在设置中选择本地 JSON 主页或 HTTPS 文档。主页只展示文字和链接。",
                []
            );
        byte[] bytes;
        if (Path.IsPathFullyQualified(location))
        {
            // [实现] 条件成立（Path.IsPathFullyQualified(location)）时：按 new FileInfo(location).Length > 1_048_576 分支处理；执行 bytes = await File.ReadAllBytesAsync(location, ct)。
            if (new FileInfo(location).Length > 1_048_576)
                throw new InvalidDataException("主页不能超过 1 MiB。");
            bytes = await File.ReadAllBytesAsync(location, ct);
        }
        else
        {
            // [实现] 前一条件不成立时：准备 var handler = new HttpClientHandler { AllowAutoRedirect = false }；准备 var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) }；准备 var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct)。
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            using var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                // [实现] 循环满足 (n = await stream.ReadAsync(buffer, ct)) > 0 时：按 memory.Length + n > 1_048_576 分支处理；执行 memory.Write(buffer, 0, n)。
                if (memory.Length + n > 1_048_576)
                    throw new InvalidDataException("主页不能超过 1 MiB。");
                memory.Write(buffer, 0, n);
            }

            bytes = memory.ToArray();
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes)
            .TrimStart();
        if (text.StartsWith("<", StringComparison.Ordinal))
        {
            return HomeMarkup.Document(text);
        }

        var page = JsonConvert.DeserializeObject<HomeDocument>(
            System.Text.Encoding.UTF8.GetString(bytes)
        ) ?? throw new InvalidDataException("主页格式无效。");
        page.Validate();
        return page;
    }

    // [实现] PrepareGameLanguage 的实现：按 !Load() .FirstLaunchChinese 分支处理；准备 var file = ResourceInstaller.SafeTarget(gameDirectory, "options.txt")；准备 var lines = File.Exists(file) ? File.ReadAllLines(file) .ToList() : []。
    public void PrepareGameLanguage(string gameDirectory, string gameVersion)
    {
        if (!Load()
            .FirstLaunchChinese)
            return;
        var file = ResourceInstaller.SafeTarget(gameDirectory, "options.txt");
        var lines = File.Exists(file)
            ? File.ReadAllLines(file)
                .ToList()
            : [];
        if (lines.Any(
            x => x.StartsWith("lang:", StringComparison.Ordinal)
        ))
            return;
        var old = Version.TryParse(gameVersion, out var version) && version.Major == 1 && version.Minor <= 10;
        lines.Add(
            "lang:" + (old
                ? "zh_CN"
                : "zh_cn")
        );
        Directory.CreateDirectory(gameDirectory);
        MeloongCore.FileUtils.WriteAtomic(
            file,
            string.Join(Environment.NewLine, lines) + Environment.NewLine
        );
    }

    // [实现] Export 的实现：按 File.Exists(target) 分支处理；准备 var launch = new LauncherSettingsStore(paths).Global；执行 JsonFiles.Write( target, new { schemaVersion = 1, appearance = Load()with { Background = "", Ba…。
    public void Export(string target)
    {
        if (File.Exists(target))
            throw new IOException("请选择新文件名。");
        var launch = new LauncherSettingsStore(paths).Global;
        // Portable configuration never includes accounts, tokens, server addresses or executable arguments.
        JsonFiles.Write(
            target,
            new
            {
                schemaVersion = 1,
                appearance = Load()with
                {
                    Background = "",
                    BackgroundDirectory = "",
                    Music = "",
                    MusicDirectory = "",
                    TitleImage = "",
                    HomeDocument = ""
                },
                launch = launch with
                {
                    JavaExecutable = "",
                    JvmArguments = "",
                    GameArguments = "",
                    ServerAddress = "",
                    QuickPlayWorld = ""
                }
            }
        );
    }

    // [实现] Import 的实现：按 new FileInfo(file).Length > 1_048_576 分支处理；准备 var json = JsonFiles.Read(file)；按 (int? )json["schemaVersion"] != 1 分支处理。
    public void Import(string file)
    {
        if (new FileInfo(file).Length > 1_048_576)
            throw new InvalidDataException("设置文件过大。");
        var json = JsonFiles.Read(file);
        if ((int? )json["schemaVersion"] != 1)
            throw new InvalidDataException("不支持的设置格式。");
        var appearance = json["appearance"]?.ToObject<AppearanceSettings>() ?? throw new InvalidDataException("缺少外观设置。");
        var launch = json["launch"]?.ToObject<LaunchSettings>() ?? throw new InvalidDataException("缺少启动设置。");
        appearance = appearance with
        {
            Background = "",
            BackgroundDirectory = "",
            Music = "",
            MusicDirectory = "",
            TitleImage = "",
            HomeDocument = ""
        };
        launch = launch with
        {
            JavaExecutable = "",
            JvmArguments = "",
            GameArguments = "",
            ServerAddress = "",
            QuickPlayWorld = ""
        };
        appearance.Validate();
        launch.Validate();
        var backup = Path.Combine(
            paths.Root,
            "settings-backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid()
                .ToString("N")[..8]
        );
        Directory.CreateDirectory(backup);
        foreach (var name in new[]
        {
            "appearance.json",
            "launch-settings.json"
        }

        )
            if (File.Exists(
                Path.Combine(paths.Root, name)
            ))
                File.Copy(
                    Path.Combine(paths.Root, name),
                    Path.Combine(backup, name)
                );
        try
        {
            // [实现] 在异常保护范围内：执行 new LauncherSettingsStore(paths).SaveGlobal(launch)；执行 Save(appearance)。
            new LauncherSettingsStore(paths).SaveGlobal(launch);
            Save(appearance);
        }
        catch
        {
            // [实现] 捕获 空 后：遍历 new[] { "appearance.json", "launch-settings.json" } 处理 name；重新抛出当前异常。
            foreach (var name in new[]
            {
                "appearance.json",
                "launch-settings.json"
            }

            )
            {
                // [实现] 对 new[] { "appearance.json", "launch-settings.json" } 中的每一项：准备 var target = Path.Combine(paths.Root, name)；按 File.Exists( Path.Combine(backup, name) ) 分支处理。
                var target = Path.Combine(paths.Root, name);
                if (File.Exists(
                    Path.Combine(backup, name)
                ))
                    File.Copy(
                        Path.Combine(backup, name),
                        target,
                        true
                    );
                else if (File.Exists(target))
                    File.Delete(target);
            }

            throw;
        }
    }

    // [实现] HelpFiles 的实现：计算并返回 Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.md") .Where( f => Path.GetFileNa…。
    public static string[] HelpFiles(string folder, string query) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*.md")
            .Where(
                f => Path.GetFileName(f)
                    .Contains(query, StringComparison.OrdinalIgnoreCase) || File.ReadAllText(f)
                    .Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .Order()
            .ToArray()
        : [];
}
