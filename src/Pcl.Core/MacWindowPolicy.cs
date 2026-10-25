namespace Pcl.Core;
/// <summary>将窗口设置交给随应用构建的本机 JVM agent；参数通过独立 JSON 文件传递，避免命令行转义问题。</summary>
public static class MacWindowPolicy
{
    // [实现] ResolveSize 的实现：按 settings.WindowMode == "default" 分支处理；准备 var file = Path.Combine(paths.Root, "launcher-window.json")；按 settings.WindowMode == "launcher" && File.Exists(file) 分支处理。
    public static LaunchSettings ResolveSize(LaunchSettings settings, AppPaths paths)
    {
        if (settings.WindowMode == "default")
        {
            // [实现] 条件成立（settings.WindowMode == "default"）时：返回 settings with { Width = 854, Height = 480 }。
            return settings with
            {
                Width = 854,
                Height = 480
            };
        }

        var file = Path.Combine(paths.Root, "launcher-window.json");
        if (settings.WindowMode == "launcher" && File.Exists(file))
        {
            // [实现] 条件成立（settings.WindowMode == "launcher" && File.Exists(file)）时：准备 var size = JsonFiles.Read(file)；返回 settings with { Width = Math.Clamp((int? )size["width"] ?? settings.Width, 320, 16384), Height …。
            var size = JsonFiles.Read(file);
            return settings with
            {
                Width = Math.Clamp((int? )size["width"] ?? settings.Width, 320, 16384),
                Height = Math.Clamp((int? )size["height"] ?? settings.Height, 240, 16384)
            };
        }

        return settings;
    }

    // [实现] Arguments 的实现：按 !OperatingSystem.IsMacOS() || (settings.GameWindowTitle.Length == 0 && settings.WindowMode != "… 分支处理；准备 var library = Path.Combine(AppContext.BaseDirectory, "libpclwindow.dylib")；按 !File.Exists(library) 分支处理。
    public static string[] Arguments(LaunchSettings settings, AppPaths paths)
    {
        if (!OperatingSystem.IsMacOS() || (settings.GameWindowTitle.Length == 0 && settings.WindowMode != "maximized" && settings.ProcessPriority == "normal"))
        {
            // [实现] 条件成立（!OperatingSystem.IsMacOS() || (settings.GameWindowTitle.Length == 0 && settings.WindowMode != "…）时：返回 []。
            return[];
        }

        var library = Path.Combine(AppContext.BaseDirectory, "libpclwindow.dylib");
        if (!File.Exists(library))
        {
            // [实现] 条件成立（!File.Exists(library)）时：抛出 new FileNotFoundException("缺少 Mac 窗口组件，请重新安装完整应用。", library)。
            throw new FileNotFoundException("缺少 Mac 窗口组件，请重新安装完整应用。", library);
        }

        var file = Path.Combine(
            paths.Evidence,
            "window-policy-" + Guid.NewGuid()
                .ToString("N") + ".json"
        );
        JsonFiles.Write(
            file,
            new
            {
                title = settings.GameWindowTitle,
                foreground = settings.ProcessPriority == "high",
                maximize = settings.WindowMode == "maximized",
                priority = settings.ProcessPriority == "low"
                    ? 10
                    : 0,
                receipt = file + ".result.json"
            }
        );
        return[
            "-agentpath:" + library + "=" + file
        ];
    }
}
