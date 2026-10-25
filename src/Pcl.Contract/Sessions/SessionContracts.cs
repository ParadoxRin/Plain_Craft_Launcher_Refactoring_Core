using System.Text.Json;
using Pcl.Core.Contracts.Accounts;

namespace Pcl.Core.Contracts.Sessions;
// 连接恢复只返回公开状态与原 Host 的逻辑游戏句柄；不包含令牌或 Java 参数。
public sealed record ClientIdentity(string ClientId, int ProcessId, long StartedUtcTicks);
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
