namespace Pcl.Core.Contracts.Accounts;
// 账户索引和短时授权提示可展示；DeviceCode、access/refresh token 永远不进入此契约。
public sealed record AccountInfo(
    string Id,
    int Kind,
    string Name,
    string Uuid,
    string? ClientId,
    bool Remembered,
    string? Server,
    string? Identifier,
    string? ServerName,
    bool HasCredential
);
public sealed record AccountSnapshot(AccountInfo[] Accounts, string? SelectedId);
public sealed record LoginPrompt(string UserCode, string VerificationUri, int ExpiresIn);
public sealed record LoginSnapshot(
    string OperationId,
    string State,
    LoginPrompt? Prompt = null,
    string? AccountId = null,
    string? ErrorCode = null
);
public sealed record SkinInfo(string Id, string State, string Url, string Variant)
{
    public override string ToString() => Variant + (State == "ACTIVE"
        ? " *"
        : "");
}

public sealed record CapeInfo(string Id, string State, string Url, string Name)
{
    public override string ToString() => Name + (State == "ACTIVE"
        ? " *"
        : "");
}

public sealed record AccountAppearance(string Id, string Name, SkinInfo[] Skins, CapeInfo[] Capes);
public sealed record GameDetails(string GameProcessId, int Pid, bool Detached, string LogFile);
// 外观网络操作可显式取消；外部服务修改的结果只供当前 Host 查询，断连不自动重放。
public sealed record AppearanceSnapshot(
    string OperationId,
    string State,
    AccountAppearance? Profile = null,
    string? ErrorCode = null
);
