using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using MeloongCore.Extensions;
using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 状态集合：AccountKind 限定为 Offline、Microsoft、External。
public enum AccountKind
{
    Offline,
    Microsoft,
    External
}

// [实现] 数据约定：LauncherAccount 汇集 Id、Kind、Name、Uuid、ClientId、Remembered、Server、Identifier、ServerName，供该模块传递状态。
public sealed record LauncherAccount(
    string Id,
    AccountKind Kind,
    string Name,
    string Uuid,
    string? ClientId = null,
    bool Remembered = false,
    string? Server = null,
    string? Identifier = null,
    string? ServerName = null
)
{
    // [实现] ToString 的实现：计算并返回 Name + (Kind == AccountKind.External ? " · " + (ServerName ?? Server) : "") + (Kind != AccountK…。
    public override string ToString() => Name + (Kind == AccountKind.External
        ? " · " + (ServerName ?? Server)
        : "") + (Kind != AccountKind.Offline && !Remembered
        ? " · 不记住登录"
        : "");
}

// [实现] 数据约定：AccountIndex 汇集 Version、SelectedId、Accounts，供该模块传递状态。
public sealed record AccountIndex(int Version, string? SelectedId, IReadOnlyList<LauncherAccount> Accounts);
// 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。
public sealed class AccountManager
{
    public AppPaths Paths { get; }

    private readonly string path;
    private readonly IAccountSecrets secrets;
    private AccountIndex index;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MicrosoftLogin> sessions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> refreshGates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExternalLogin> externalSessions = new();
    // [实现] AccountManager 构造 的实现：执行 Paths = paths；执行 path = Path.Combine(paths.Root, "accounts.json")；执行 this.secrets = secrets。
    public AccountManager(AppPaths paths, IAccountSecrets secrets)
    {
        Paths = paths;
        path = Path.Combine(paths.Root, "accounts.json");
        this.secrets = secrets;
        index = File.Exists(path)
            ? JsonConvert.DeserializeObject<AccountIndex>(
                File.ReadAllText(path)
            ) ?? throw new InvalidDataException("账户列表损坏。")
            : new(
                1,
                null,
                Array.Empty<LauncherAccount>()
            );
        if (index.Version != 1 || index.Accounts.Count > 200 || index.Accounts.Any(
            x => !Enum.IsDefined(x.Kind) || x.Id != (x.Kind == AccountKind.External
                ? ExternalAuthentication.Identity(x.Server ?? "", x.Identifier ?? "", x.Uuid)
                : (x.Kind == AccountKind.Offline
                    ? "offline-"
                    : "microsoft-") + x.Uuid) || x.Uuid.Length != 32 || !x.Uuid.All(Uri.IsHexDigit)
        ) || index.Accounts.Select(
            x => x.Id
        )
            .Distinct()
            .Count() != index.Accounts.Count)
            throw new InvalidDataException("账户列表格式无效，原文件已保留。");
    }

    // 客户端只缓存公开的会话可用性，不获取 Host 的令牌；宿主仍进行实际授权校验。
    public Func<string, bool>? RemoteCredentialAvailable { get; set; }
    public Func<bool>? CanWriteIndex { get; set; }

    public void ReloadIndex()
    {
        var reloaded = new AccountManager(Paths, secrets);
        index = reloaded.index;
        foreach (var id in sessions.Keys.Where(
            id => !index.Accounts.Any(
                x => x.Id == id
            )
        ))
        {
            sessions.TryRemove(id, out _);
        }

        foreach (var id in externalSessions.Keys.Where(
            id => !index.Accounts.Any(
                x => x.Id == id
            )
        ))
        {
            externalSessions.TryRemove(id, out _);
        }
    }

    public IReadOnlyList<LauncherAccount> Accounts => index.Accounts;
    public string? SelectedId => index.SelectedId;

    /// <summary>供界面判断所选微软账户是否有可尝试恢复的凭据；不读取钥匙串，也不替代联网权益校验。</summary>
    public bool HasMicrosoftCredential(string id)
    {
        if (RemoteCredentialAvailable?.Invoke(id) == true)
        {
            return true;
        }

        var account = Accounts.SingleOrDefault(
            item => item.Id == id
        );
        if (account?.Kind != AccountKind.Microsoft)
        {
            // [实现] 条件成立（account?.Kind != AccountKind.Microsoft）时：返回 false。
            return false;
        }

        // “记住登录”允许启动时读取钥匙串；实际读取失败仍由 ResolveAsync 拒绝。
        if (account.Remembered)
        {
            // [实现] 条件成立（account.Remembered）时：返回 true。
            return true;
        }

        // 仅本次会话不能依赖账户记录，必须有访问令牌或可刷新的内存会话。
        if (!sessions.TryGetValue(id, out var login))
        {
            // [实现] 条件成立（!sessions.TryGetValue(id, out var login)）时：返回 false。
            return false;
        }

        return login.ExpiresAt > DateTimeOffset.UtcNow || !string.IsNullOrWhiteSpace(login.RefreshToken);
    }

    // [实现] ValidateOfflineName 的实现：执行 name = name.Trim()；按 name.Length is < 1 or > 16 || name.Any(char.IsControl) || name.Contains('"') 分支处理；返回 name。
    public static string ValidateOfflineName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 16 || name.Any(char.IsControl) || name.Contains('"'))
            throw new InvalidDataException("离线玩家名需要 1–16 个字符，不能包含英文引号或控制字符。");
        return name;
    }

    // [实现] OfflineSession 的实现：执行 name = ValidateOfflineName(name)；准备 var full = name.Length.ToString("X") .PadLeft(16, '0') + name.GetStableHashCode() .ToString("X"…；准备 var uuid = (full[..12] + "3" + full[13..16] + "9" + full[17..]).ToLowerInvariant()。
    public static GameSession OfflineSession(string name, string model = "auto")
    {
        name = ValidateOfflineName(name);
        // PCL2 ModLaunch.McLoginLegacyUuid, using its existing MeloongCore stable hash.
        var full = name.Length.ToString("X")
            .PadLeft(16, '0') + name.GetStableHashCode()
            .ToString("X")
            .PadLeft(16, '0');
        var uuid = (full[..12] + "3" + full[13..16] + "9" + full[17..]).ToLowerInvariant();
        // UUID.hashCode 的奇偶决定旧版模型；现代默认皮肤数组的前九项为 slim，后九项为 wide。
        if (model is "wide" or "slim")
        {
            for (var attempt = 0; attempt < 1048576; attempt++)
            {
                var hash = unchecked((int)(Convert.ToUInt32(uuid[..8], 16) ^ Convert.ToUInt32(uuid[8..16], 16) ^ Convert.ToUInt32(uuid[16..24], 16) ^ Convert.ToUInt32(uuid[24..], 16)));
                var modern = ((hash % 18) + 18) % 18 < 9;
                if (((hash & 1) == 1) == (model == "slim") && modern == (model == "slim"))
                {
                    break;
                }

                var tail = (Convert.ToUInt32(uuid[27..], 16) + 1) & 0xfffff;
                uuid = uuid[..27] + tail.ToString("x5");
            }
        }

        return new(name, uuid, uuid, "legacy", false);
    }

    // [实现] Save 的实现：执行 JsonFiles.Write(path, next)；执行 index = next。
    private void Save(AccountIndex next)
    {
        JsonFiles.Write(path, next);
        index = next;
    }

    // [实现] Upsert 的实现：执行 Save( index with { SelectedId = account.Id, Accounts = new[] { account }.Concat( index.Accounts…；返回 account。
    private LauncherAccount Upsert(LauncherAccount account)
    {
        Save(
            index with
            {
                SelectedId = account.Id,
                Accounts = new[]
                {
                    account
                }.Concat(
                    index.Accounts.Where(
                        x => x.Id != account.Id
                    )
                )
                    .ToArray()
            }
        );
        return account;
    }

    // [实现] AddOffline 的实现：准备 var session = OfflineSession(name)；返回 Upsert( new("offline-" + session.Uuid, AccountKind.Offline, session.Name, session.Uuid) )。
    public LauncherAccount AddOffline(string name)
    {
        var session = OfflineSession(name);
        return Upsert(
            new("offline-" + session.Uuid, AccountKind.Offline, session.Name, session.Uuid)
        );
    }

    // [实现] Select 的实现：执行 _ = Find(id)；执行 Save( index with { SelectedId = id } )。
    public void Select(string id)
    {
        if (CanWriteIndex?.Invoke() == false)
        {
            return;
        }

        _ = Find(id);
        Save(
            index with
            {
                SelectedId = id
            }
        );
    }

    // [实现] Find 的实现：计算并返回 Accounts.SingleOrDefault( x => x.Id == id ) ?? throw new InvalidOperationException("请选择一个账户。")。
    private LauncherAccount Find(string id) => Accounts.SingleOrDefault(
        x => x.Id == id
    ) ?? throw new InvalidOperationException("请选择一个账户。");
    // [实现] SaveMicrosoft 的实现：准备 var id = "microsoft-" + login.Session.Uuid；准备 var saved = false；按 remember && !string.IsNullOrWhiteSpace(login.RefreshToken) 分支处理。
    public LauncherAccount SaveMicrosoft(MicrosoftLogin login, bool remember)
    {
        var id = "microsoft-" + login.Session.Uuid;
        var saved = false;
        if (remember && !string.IsNullOrWhiteSpace(login.RefreshToken))
        {
            // [实现] 条件成立（remember && !string.IsNullOrWhiteSpace(login.RefreshToken)）时：执行 secrets.Set(id, login.RefreshToken)；执行 saved = true。
            secrets.Set(id, login.RefreshToken);
            saved = true;
        }
        else
            secrets.Delete(id);
        var account = Upsert(
            new(
                id,
                AccountKind.Microsoft,
                login.Session.Name,
                login.Session.Uuid,
                login.ClientId,
                saved
            )
        );
        sessions[id] = login;
        Pcl.Core.Adapters.Launch.LaunchGuidanceState.RecordVerified(Paths, login.Session);
        return account;
    }

    // [实现] SaveSession 的实现：准备 var id = "microsoft-" + login.Session.Uuid；准备 var account = Upsert( new( id, AccountKind.Microsoft, login.Session.Name, login.Session.Uuid, l…；执行 sessions[id] = login。
    public LauncherAccount SaveSession(MicrosoftLogin login)
    {
        var id = "microsoft-" + login.Session.Uuid;
        var account = Upsert(
            new(
                id,
                AccountKind.Microsoft,
                login.Session.Name,
                login.Session.Uuid,
                login.ClientId,
                false
            )
        );
        sessions[id] = login;
        Pcl.Core.Adapters.Launch.LaunchGuidanceState.RecordVerified(Paths, login.Session);
        return account;
    }

    // [实现] ResolveAsync 的实现：准备 var account = Find(id)；按 account.Kind == AccountKind.Offline 分支处理；按 account.Kind == AccountKind.External 分支处理。
    public async Task<GameSession> ResolveAsync(string id, MicrosoftAuthentication auth, CancellationToken ct = default)
    {
        var account = Find(id);
        if (account.Kind == AccountKind.Offline)
            return OfflineSession(
                account.Name,
                new OfflineSkinService(Paths).Get(account)?.Model ?? "auto"
            );
        if (account.Kind == AccountKind.External)
        {
            // [实现] 条件成立（account.Kind == AccountKind.External）时：准备 var external = new ExternalAuthentication()；返回 (await ResolveExternalAsync(id, external, ct)).Credential.Session。
            using var external = new ExternalAuthentication();
            return (await ResolveExternalAsync(id, external, ct)).Credential.Session;
        }

        // 启动准备已在后台执行；同账户刷新串行，缓存读取可与只读界面状态查询并发。
        var refreshGate = refreshGates.GetOrAdd(
            id,
            _ => new SemaphoreSlim(1, 1)
        );
        await refreshGate.WaitAsync(ct);
        try
        {
            if (sessions.TryGetValue(id, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
                return cached.Session;
            var token = cached?.RefreshToken ?? (account.Remembered
                ? secrets.Get(id)
                : null);
            if (token is null)
                throw new LoginRequiredException("请先登录此微软账户。");
            var refreshed = await auth.RefreshAsync(account.ClientId!, token, ct);
            if (refreshed.Session.Uuid != account.Uuid)
                throw new LoginRequiredException("刷新后的账户身份不一致，请重新登录。");
            SaveMicrosoft(refreshed, account.Remembered);
            return refreshed.Session;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    // [实现] SaveExternal 的实现：准备 var session = login.Credential.Session；准备 var id = ExternalAuthentication.Identity(login.Server.ApiRoot, login.Identifier, session.Uuid)；按 remember 分支处理。
    public LauncherAccount SaveExternal(ExternalLogin login, bool remember)
    {
        var session = login.Credential.Session;
        var id = ExternalAuthentication.Identity(login.Server.ApiRoot, login.Identifier, session.Uuid);
        if (remember)
            secrets.Set(
                id,
                JsonConvert.SerializeObject(login.Credential)
            );
        else if (Accounts.Any(
            x => x.Id == id && x.Remembered
        ))
            secrets.Delete(id);
        return SaveExternalSession(login, remember);
    }

    // [实现] SaveExternalSession 的实现：准备 var session = login.Credential.Session；准备 var id = ExternalAuthentication.Identity(login.Server.ApiRoot, login.Identifier, session.Uuid)；准备 var account = Upsert( new( id, AccountKind.External, session.Name, session.Uuid, Remembered: re…。
    public LauncherAccount SaveExternalSession(ExternalLogin login, bool remembered = false)
    {
        var session = login.Credential.Session;
        var id = ExternalAuthentication.Identity(login.Server.ApiRoot, login.Identifier, session.Uuid);
        var account = Upsert(
            new(
                id,
                AccountKind.External,
                session.Name,
                session.Uuid,
                Remembered: remembered,
                Server: login.Server.ApiRoot,
                Identifier: login.Identifier,
                ServerName: login.Server.Name
            )
        );
        externalSessions[id] = login;
        return account;
    }

    // [实现] ExternalCredentialFor 的实现：按 externalSessions.TryGetValue(account.Id, out var cached) 分支处理；按 account.Remembered && secrets.Get(account.Id)is string value 分支处理；抛出 new LoginRequiredException("请先登录此外置账号。")。
    private ExternalCredential ExternalCredentialFor(LauncherAccount account)
    {
        if (externalSessions.TryGetValue(account.Id, out var cached))
            return cached.Credential;
        if (account.Remembered && secrets.Get(account.Id)is string value)
            return JsonConvert.DeserializeObject<ExternalCredential>(value) ?? throw new LoginRequiredException("外置账号凭据损坏，请重新登录。");
        throw new LoginRequiredException("请先登录此外置账号。");
    }

    // [实现] ResolveExternalAsync 的实现：准备 var account = Find(id)；按 account.Kind != AccountKind.External 分支处理；准备 var credential = ExternalCredentialFor(account)。
    public async Task<ExternalLogin> ResolveExternalAsync(string id, ExternalAuthentication auth, CancellationToken ct = default)
    {
        var account = Find(id);
        if (account.Kind != AccountKind.External)
            throw new InvalidOperationException("请选择外置账号。");
        var credential = ExternalCredentialFor(account);
        if (credential.Session.Uuid != account.Uuid)
            throw new LoginRequiredException("外置账号身份记录不一致。");
        var server = await auth.DiscoverAsync(account.Server!, ct);
        if (server.ApiRoot != account.Server)
            throw new LoginRequiredException("外置认证地址已改变，请重新确认服务器。");
        var result = await auth.ValidateOrRefreshAsync(server, account.Identifier!, credential, ct);
        SaveExternal(result, account.Remembered);
        return result;
    }

    // [实现] ExternalLaunchArgumentsAsync 的实现：准备 var account = Find(id)；按 account.Kind != AccountKind.External 分支处理；准备 var auth = new ExternalAuthentication()。
    public async Task<IReadOnlyList<string>> ExternalLaunchArgumentsAsync(string id, MinecraftService service, CancellationToken ct = default)
    {
        var account = Find(id);
        if (account.Kind != AccountKind.External)
            return[];
        using var auth = new ExternalAuthentication();
        var server = await auth.DiscoverAsync(account.Server!, ct);
        if (server.ApiRoot != account.Server)
            throw new LoginRequiredException("外置认证地址已改变，请重新确认服务器。");
        return await auth.LaunchArgumentsAsync(server, service, ct);
    }

    // [实现] LogoutExternalAsync 的实现：准备 var account = Find(id)；按 account.Kind != AccountKind.External 分支处理；准备 var credential = ExternalCredentialFor(account)。
    public async Task LogoutExternalAsync(string id, ExternalAuthentication auth, CancellationToken ct = default)
    {
        var account = Find(id);
        if (account.Kind != AccountKind.External)
        {
            // [实现] 条件成立（account.Kind != AccountKind.External）时：执行 Logout(id)；结束当前过程。
            Logout(id);
            return;
        }

        var credential = ExternalCredentialFor(account);
        try
        {
            // [实现] 在异常保护范围内：执行 await auth.InvalidateAsync( new(account.Server!, account.ServerName ?? "", "{}"), credential, c…。
            await auth.InvalidateAsync(
                new(account.Server!, account.ServerName ?? "", "{}"),
                credential,
                ct
            );
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Logout(id)。
            Logout(id);
        }
    }

    // [实现] Logout 的实现：准备 var account = Find(id)；按 account.Kind == AccountKind.Offline 分支处理；按 account.Remembered 分支处理。
    public void Logout(string id)
    {
        var account = Find(id);
        if (account.Kind == AccountKind.Offline)
            return;
        if (account.Remembered)
            secrets.Delete(id);
        sessions.TryRemove(id, out _);
        externalSessions.TryRemove(id, out _);
        Upsert(
            account with
            {
                Remembered = false
            }
        );
    }

    // [实现] Remove 的实现：准备 var account = Find(id)；按 account.Kind != AccountKind.Offline && account.Remembered 分支处理；执行 sessions.TryRemove(id, out _)。
    public void Remove(string id)
    {
        var account = Find(id);
        if (account.Kind != AccountKind.Offline && account.Remembered)
            secrets.Delete(id);
        sessions.TryRemove(id, out _);
        externalSessions.TryRemove(id, out _);
        var all = Accounts.Where(
            x => x.Id != id
        )
            .ToArray();
        Save(
            index with
            {
                SelectedId = index.SelectedId == id
                    ? all.FirstOrDefault()?.Id
                    : index.SelectedId,
                Accounts = all
            }
        );
    }
}
