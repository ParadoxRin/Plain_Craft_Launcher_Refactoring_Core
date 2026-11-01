using System.Reflection;
using System.Runtime.InteropServices;
using Pcl.Core.Contracts.Updates;

namespace Pcl.Host;
// 纯构建声明：探针在创建数据目录、系统凭据和任务恢复服务之前返回。
internal static class HostBuildInfo
{
    internal static readonly string[] Capabilities =
    [
        "page.operations.v1", "workspace.operations.v1", "blob.file.v1", "version.install.v3",
        "game.launch.v2", "game.guidance.v1", "session.reconnect.v1", "event.subscribe.v1",
        "request.ordered-ids.v1", "operation.snapshot", "operation.cancel", "blob.read", "blob.page",
        "operation.log.snapshot", "desktop.contract-only.v1", "account.login.v1", "game.details",
        "host.update.prepare.v1", "content.update.v1"
    ];
    internal static HostBuild Build => new(
        typeof(HostBuildInfo).Assembly.GetName().Version!.ToString(3),
        long.Parse(typeof(HostBuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "HostUpdateSequence").Value!), 1,
        (OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux") + "-" +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
}
