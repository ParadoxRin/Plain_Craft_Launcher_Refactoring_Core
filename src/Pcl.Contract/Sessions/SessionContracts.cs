using System.Text.Json;
using Pcl.Core.Contracts.Accounts;

namespace Pcl.Core.Contracts.Sessions;
// 连接恢复只返回公开状态与原 Host 的逻辑游戏句柄；不包含令牌或 Java 参数。
// Linux 使用 /proc/<pid>/stat 的原始启动 tick，避免各进程估算 boot UTC 的舍入差异。
// StartedUtcTicks 保留原 UTC 含义；Linux 必须提供 LinuxStartTicks，不回退到 PID 或时间容差。
public sealed record ClientIdentity(string ClientId, int ProcessId, long StartedUtcTicks, ulong? LinuxStartTicks = null);
public sealed record ActiveGame(GameDetails Details, string InstanceId, string AccountId);
public sealed record SessionAttachment(string HostId, int HostPid, string? LegacyGrant, ActiveGame[] Games, bool Resumed);
public sealed record EventSubscription(string SubscriptionId, string HostId, string Kind, string SubjectId);
public sealed record HostEvent(
    string SubscriptionId,
    string HostId,
    string Kind,
    string SubjectId,
    long Sequence,
    JsonElement Snapshot
);
