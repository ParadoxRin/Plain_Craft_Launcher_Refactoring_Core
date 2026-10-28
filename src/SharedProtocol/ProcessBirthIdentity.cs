using System.Diagnostics;
using System.Globalization;
using Pcl.Core.Contracts.Sessions;

namespace Pcl.Protocol;

// 仅封装本机连接的进程出生标识；不包含业务规则，也不承担跨用户认证。
internal static class ProcessBirthIdentity
{
    internal static ClientIdentity Current()
    {
        using var process = Process.GetCurrentProcess();
        return new ClientIdentity(Guid.NewGuid().ToString("N"), process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            OperatingSystem.IsLinux() ? LinuxStart(process.Id) : null);
    }

    internal static bool Matches(Process process, ClientIdentity identity)
    {
        if (OperatingSystem.IsLinux())
        {
            // 精确比较内核的启动 tick；旧客户端缺少证明时安全拒绝，不能按 PID 接管。
            return identity.LinuxStartTicks is > 0 && identity.LinuxStartTicks == LinuxStart(process.Id);
        }

        // Windows/macOS 保留原生进程启动时间的精确比较，不改变现有会话行为。
        return process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
    }

    private static ulong LinuxStart(int pid)
    {
        var stat = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat");
        // comm 可含空格和右括号；最后一个右括号之后才是字段 3 起的固定数字布局。
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open <= 0 || close <= open || !int.TryParse(stat.AsSpan(0, open).Trim(),
            NumberStyles.None, CultureInfo.InvariantCulture, out var actualPid) || actualPid != pid)
        {
            throw new IOException("PROCESS_IDENTITY_UNAVAILABLE");
        }

        var fields = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], NumberStyles.None,
            CultureInfo.InvariantCulture, out var start) || start == 0)
        {
            throw new IOException("PROCESS_IDENTITY_UNAVAILABLE");
        }

        return start;
    }
}
