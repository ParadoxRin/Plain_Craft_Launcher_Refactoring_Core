namespace Pcl.Core.Ports.Accounts;
// 内部账户边界只返回索引和短时授权提示，认证令牌始终留在执行适配器及钥匙串。
public sealed record AccountEntry(
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
public sealed record AccountIndex(AccountEntry[] Accounts, string? SelectedId);
public sealed record AccountChallenge(string UserCode, string VerificationUri, int ExpiresIn);
public sealed record AccountSkin(string Id, string State, string Url, string Variant);
public sealed record AccountCape(string Id, string State, string Url, string Name);
public sealed record AccountLook(string Id, string Name, AccountSkin[] Skins, AccountCape[] Capes);
public interface IAccountBackend
{
    AccountIndex Read();
    Task<string> LoginAsync(bool remember, Action<AccountChallenge> challenge, CancellationToken ct);
    void Logout(string id, bool remove);
    Task<AccountLook?> AppearanceAsync(string id, string action, string? value, byte[]? png, bool slim, CancellationToken ct);
}
