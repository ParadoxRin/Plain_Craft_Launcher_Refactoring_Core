using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：TargetPlatform 汇集 Os、Architecture、OsVersion，供该模块传递状态。
// 共享数据模型：定义实例、下载、平台和路径的数据约定，供核心服务与界面交换状态。
public sealed class AppPaths
{
    public string Root { get; }
    public string Game => Path.Combine(Root, "minecraft");
    public string Runtime => Path.Combine(Root, "runtime");
    public string Evidence => Path.Combine(Root, "evidence");

    // [实现] AppPaths 构造 的实现：执行 Root = Path.GetFullPath( root ?? Path.Combine( Environment.GetFolderPath(Environment.SpecialFol…；执行 Directory.CreateDirectory(Root)；执行 Directory.CreateDirectory(Evidence)。
    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                OperatingSystem.IsMacOS()
                    ? "Library/Application Support/PCL-MacPort"
                    : ".pcl-mac-port"
            )
        );
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Evidence);
    }

    // [实现] VersionFolder 的实现：计算并返回 SafeChild( Path.Combine(Game, "versions"), version )。
    public string VersionFolder(string version) => SafeChild(
        Path.Combine(Game, "versions"),
        version
    );
    // [实现] SafeChild 的实现：按 string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) 分支处理；准备 var full = Path.GetFullPath( Path.Combine( root, relative.Replace('\\', '/') ) )；按 !MeloongCore.PathUtils.IsParentOf(root, full) || MeloongCore.PathUtils.IsSame(root, full) 分支处理。
    public static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("需要相对路径。");
        var full = Path.GetFullPath(
            Path.Combine(
                root,
                relative.Replace('\\', '/')
            )
        );
        if (!MeloongCore.PathUtils.IsParentOf(root, full) || MeloongCore.PathUtils.IsSame(root, full))
            throw new InvalidDataException("路径超出目标目录。");
        return full;
    }
}

// [实现] 数据约定：DownloadItem 汇集 Url、Path、Size、Sha1、ExtractNative，供该模块传递状态。
public sealed record DownloadItem(Uri Url, string Path, long Size, string? Sha1, bool ExtractNative = false);
// [实现] 数据约定：InstalledVersion 汇集 Id、JsonPath、JarPath、AssetIndex、Libraries、JavaMajor、LibraryRoot，供该模块传递状态。
public sealed record InstalledVersion(
    string Id,
    string JsonPath,
    string JarPath,
    string AssetIndex,
    IReadOnlyList<DownloadItem> Libraries,
    int JavaMajor,
    string? LibraryRoot = null
);
// [实现] 数据约定：JavaRuntime 汇集 Executable、Architecture、MajorVersion，供该模块传递状态。
public sealed record JavaRuntime(string Executable, string Architecture, int MajorVersion);
// [实现] 数据约定：GameSession 汇集 Name、Uuid、AccessToken、UserType、Demo、Xuid，供该模块传递状态。
public sealed record GameSession(
    string Name,
    string Uuid,
    string AccessToken,
    string UserType,
    bool Demo,
    string? Xuid = null
)
{
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    internal VerifiedGameOwnership? Ownership { get; init; }
    public static GameSession Trial => new("Player", "00000000000000000000000000000000", "0", "legacy", true);
}

// [实现] 数据约定：LaunchPlan 汇集 Java、Arguments、WorkingDirectory，供该模块传递状态。
public sealed record LaunchPlan(string Java, IReadOnlyList<string> Arguments, string WorkingDirectory);
// 共享数据模型：定义实例、下载、平台和路径的数据约定，供核心服务与界面交换状态。
public static class JsonFiles
{
    // [实现] Write 的实现：执行 Directory.CreateDirectory( Path.GetDirectoryName(path)! )；执行 MeloongCore.FileUtils.WriteAtomic( path, JsonConvert.SerializeObject(value, Formatting.Indented…。
    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        MeloongCore.FileUtils.WriteAtomic(
            path,
            JsonConvert.SerializeObject(value, Formatting.Indented)
        );
    }

    // [实现] Read 的实现：计算并返回 JObject.Parse( File.ReadAllText(path) )。
    public static JObject Read(string path) => JObject.Parse(
        File.ReadAllText(path)
    );
}
