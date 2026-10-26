using Pcl.Core.Ports.Accounts;

namespace Pcl.Core.Adapters.Accounts;
// 认证、皮肤 HTTP 与账户存储由此执行；任务受理、终态和关闭由 Application 负责。
public sealed class AccountBackend(AccountManager accounts) : IAccountBackend
{
    public Pcl.Core.Ports.Accounts.AccountIndex Read() => new(
        accounts.Accounts.Select(
            x => new AccountEntry(
                x.Id,
                (int)x.Kind,
                x.Name,
                x.Uuid,
                x.ClientId,
                x.Remembered,
                x.Server,
                x.Identifier,
                x.ServerName,
                x.Kind == AccountKind.Microsoft && accounts.HasMicrosoftCredential(x.Id)
            )
        )
            .ToArray(),
        accounts.SelectedId
    );
    public async Task<string> LoginAsync(bool remember, Action<AccountChallenge> prompt, CancellationToken ct)
    {
        using var auth = new MicrosoftAuthentication();
        var clientId = MicrosoftApplication.BuiltIn.RequireClientId();
        var challenge = await auth.BeginAsync(clientId, ct)
            .ConfigureAwait(false);
        prompt(
            new(challenge.UserCode, challenge.VerificationUri, challenge.ExpiresIn)
        );
        var login = await auth.CompleteLoginAsync(clientId, challenge, ct)
            .ConfigureAwait(false);
        try
        {
            return accounts.SaveMicrosoft(login, remember)
                .Id;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return accounts.SaveSession(login)
                .Id;
        }
    }

    public void Logout(string id, bool remove)
    {
        if (remove)
        {
            accounts.Remove(id);
        }
        else
        {
            accounts.Logout(id);
        }
    }

    public async Task<AccountLook?> AppearanceAsync(
        string id,
        string action,
        string? value,
        byte[]? png,
        bool slim,
        CancellationToken ct = default
    )
    {
        using var auth = new MicrosoftAuthentication();
        var session = await accounts.ResolveAsync(id, auth, ct);
        using var skins = new MicrosoftSkinService();
        switch (action)
        {
            case "get":
                var appearance = await skins.GetAsync(session, ct);
                return new AccountLook(
                    appearance.Id,
                    appearance.Name,
                    appearance.Skins.Select(
                        x => new AccountSkin(x.Id, x.State, x.Url, x.Variant)
                    )
                        .ToArray(),
                    appearance.Capes.Select(
                        x => new AccountCape(x.Id, x.State, x.Url, x.Name)
                    )
                        .ToArray()
                );
            case "skin.use":
                await skins.UseSkinAsync(session, value!, ct);
                break;
            case "skin.upload":
                await skins.UploadSkinAsync(session, png!, slim, ct);
                break;
            case "skin.reset":
                await skins.ResetSkinAsync(session, ct);
                break;
            case "cape.use":
                await skins.UseCapeAsync(session, value!, ct);
                break;
            case "cape.hide":
                await skins.HideCapeAsync(session, ct);
                break;
            default:
                throw new Pcl.Core.Ports.Operations.OperationInstallException("INVALID_ACCOUNT_ACTION");
        }

        return null;
    }
}
