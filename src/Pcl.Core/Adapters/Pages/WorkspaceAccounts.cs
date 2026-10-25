using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using Pcl.Core.Ports.Operations;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core.Adapters.Pages;
public sealed record PublicSkinRecord(string AccountId, PlayerSkinDownload Skin);
public sealed partial class PageExecutionAdapter
{
    private readonly Dictionary<string, (ExternalLoginChallenge Challenge, DateTimeOffset Expires)> externalChallenges = new();
    private AccountManager PageAccounts => pageAccounts ??= new AccountManager(
        source.Paths,
        new Pcl.Core.Adapters.Mac.KeychainAccountSecrets(source.Paths.Root)
    );

    // These actions share the owned workspace runner and the existing account store. Secrets stay in Host memory/keychain.
    private async Task<object?> ExecuteAccountPageAsync(string id, string method, JObject args, CancellationToken ct)
    {
        string Text(string name) => (string? )args[name] ?? "";
        var accounts = PageAccounts;
        var accountId = Text("accountId");
        LauncherAccount Account() => accounts.Accounts.SingleOrDefault(
            x => x.Id == accountId
        ) ?? throw new OperationInstallException("ACCOUNT_NOT_FOUND");
        LauncherAccount Offline()
        {
            var account = Account();
            if (account.Kind != AccountKind.Offline)
            {
                throw new OperationInstallException("OFFLINE_ACCOUNT_REQUIRED");
            }

            return account;
        }

        var offline = new OfflineSkinService(source.Paths);
        object Snapshot() => new
        {
            Accounts = accounts.Accounts,
            accounts.SelectedId
        };
        async Task<byte[]> ReadPng(string kind)
        {
            var file = Text("path");
            if (new FileInfo(file).Length > 4 * 1024 * 1024)
            {
                throw new OperationInstallException("IMAGE_TOO_LARGE");
            }

            var data = await File.ReadAllBytesAsync(file, ct);
            ExternalAuthentication.ValidatePng(data, kind);
            return data;
        }

        View.SkinPreview Preview(LauncherAccount account, PlayerSkinDownload skin)
        {
            Save(
                id,
                "public-skin",
                new PublicSkinRecord(account.Id, skin)
            );
            return new(
                id,
                skin.PlayerName,
                skin.Model,
                SkinPixels.Modern(skin.Png)
            );
        }

        switch (method)
        {
            case "workspace.account.list":
                return Snapshot();
            case "workspace.account.addOffline":
                return accounts.AddOffline(
                    Text("name")
                );
            case "workspace.account.select":
                accounts.Select(
                    Account()
                        .Id
                );
                return Snapshot();
            case "workspace.account.remove":
                accounts.Remove(
                    Account()
                        .Id
                );
                return Snapshot();
            case "workspace.account.logout":
                if (Account()
                    .Kind == AccountKind.External)
                {
                    using var auth = new ExternalAuthentication();
                    await accounts.LogoutExternalAsync(accountId, auth, ct);
                }
                else
                {
                    accounts.Logout(accountId);
                }

                return Snapshot();
            case "workspace.account.skinState":
                var selected = Offline();
                var info = offline.Get(selected);
                var path = offline.ImagePath(selected);
                return new View.SkinState(
                    info?.PlayerName ?? "",
                    info?.Model ?? "auto",
                    path is null
                        ? null
                        : SkinPixels.Modern(
                            await File.ReadAllBytesAsync(path, ct)
                        )
                );
            case "workspace.account.skinFetch":
                using (var publicSkins = new PublicPlayerSkin())
                {
                    return Preview(
                        Offline(),
                        await publicSkins.FetchAsync(
                            Text("name"),
                            ct
                        )
                    );
                }

            case "workspace.account.skinSaveFetched":
                var fetched = Read<PublicSkinRecord>(
                    Text("handle"),
                    "public-skin"
                );
                if (fetched.AccountId != Offline()
                    .Id)
                {
                    throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
                }

                offline.Save(
                    Offline(),
                    fetched.Skin.Png,
                    fetched.Skin.Model,
                    fetched.Skin.PlayerName
                );
                return true;
            case "workspace.account.skinRefresh":
                var name = offline.Get(
                    Offline()
                )?.PlayerName;
                if (string.IsNullOrEmpty(name))
                {
                    throw new OperationInstallException("PUBLIC_SKIN_NOT_SAVED");
                }

                using (var publicSkins = new PublicPlayerSkin())
                {
                    var latest = await publicSkins.FetchAsync(name, ct);
                    offline.Save(
                        Offline(),
                        latest.Png,
                        latest.Model,
                        latest.PlayerName
                    );
                    return Preview(
                        Offline(),
                        latest
                    );
                }

            case "workspace.account.skinPreset":
                var preset = LocalSkinPresets.ReadWithModel(
                    source.Paths,
                    Text("preset")
                );
                var pixels = SkinPixels.Modern(preset.Png);
                offline.Save(
                    Offline(),
                    pixels,
                    preset.Model
                );
                return new View.SkinState("", preset.Model, pixels);
            case "workspace.account.skinImport":
                var imported = SkinPixels.Modern(
                    await ReadPng("skin")
                );
                offline.Save(
                    Offline(),
                    imported,
                    Text("model")
                );
                return new View.SkinState(
                    "",
                    Text("model"),
                    imported
                );
            case "workspace.account.skinRemove":
                offline.Remove(
                    Offline()
                );
                return true;
            case "workspace.account.skinExport":
                var output = WorkspaceOutput(id, "skin.png");
                offline.Export(
                    Offline(),
                    output
                );
                return RegisterWorkspaceExport(id, output);
            case "workspace.account.appearance":
                if (Account()
                    .Kind != AccountKind.Microsoft)
                {
                    throw new OperationInstallException("MICROSOFT_ACCOUNT_REQUIRED");
                }

                using (var auth = new MicrosoftAuthentication())
                    using (var skins = new MicrosoftSkinService())
                        using (var images = new ExternalAuthentication())
                        {
                            var session = await accounts.ResolveAsync(accountId, auth, ct);
                            // The action allowlist prevents arbitrary authenticated requests. Writes are never replayed after an uncertain commit.
                            switch (Text("action"))
                            {
                                case "get":
                                    break;
                                case "skin.use":
                                    await skins.UseSkinAsync(
                                        session,
                                        Text("value"),
                                        ct
                                    );
                                    break;
                                case "skin.upload":
                                    await skins.UploadSkinAsync(
                                        session,
                                        SkinPixels.Modern(
                                            await ReadPng("skin")
                                        ),
                                        (bool? )args["slim"] ?? false,
                                        ct
                                    );
                                    break;
                                case "skin.reset":
                                    await skins.ResetSkinAsync(session, ct);
                                    break;
                                case "cape.use":
                                    await skins.UseCapeAsync(
                                        session,
                                        Text("value"),
                                        ct
                                    );
                                    break;
                                case "cape.hide":
                                    await skins.HideCapeAsync(session, ct);
                                    break;
                                default:
                                    throw new OperationInstallException("ACCOUNT_ACTION_INVALID");
                            }

                            var appearance = await skins.GetAsync(session, ct);
                            var active = appearance.Skins.FirstOrDefault(
                                x => x.State == "ACTIVE"
                            ) ?? appearance.Skins.FirstOrDefault();
                            var png = active is null
                                ? null
                                : SkinPixels.Modern(
                                    await images.DownloadTextureAsync(active.Url, "skin", ct)
                                );
                            return new
                            {
                                Profile = appearance,
                                Png = png
                            };
                        }

            case "workspace.account.discover":
                using (var auth = new ExternalAuthentication())
                {
                    var found = await auth.DiscoverAsync(
                        Text("address"),
                        ct
                    );
                    return new View.ExternalServer(found.ApiRoot, found.Name);
                }

            case "workspace.account.externalLogin":
                foreach (var key in externalChallenges.Where(
                    x => x.Value.Expires < DateTimeOffset.UtcNow
                )
                    .Select(
                        x => x.Key
                    )
                    .ToArray())
                {
                    externalChallenges.Remove(key);
                }

                if (externalChallenges.Count >= 8)
                {
                    throw new OperationInstallException("LOGIN_CHALLENGE_LIMIT");
                }

                using (var auth = new ExternalAuthentication())
                {
                    var password = Secret(
                        Text("secretHandle"),
                        consume: true
                    ) ?? throw new OperationInstallException("SECRET_REENTRY_REQUIRED");
                    var found = await auth.DiscoverAsync(
                        Text("address"),
                        ct
                    );
                    var challenge = await auth.AuthenticateAsync(
                        found,
                        Text("identifier"),
                        password,
                        ct
                    );
                    externalChallenges[id] = (challenge, DateTimeOffset.UtcNow.AddMinutes(10));
                    return new
                    {
                        Handle = id,
                        challenge.Profiles,
                        challenge.Selected
                    };
                }

            case "workspace.account.externalSelect":
                if (!externalChallenges.TryGetValue(
                    Text("handle"),
                    out var challengeRow
                ) || challengeRow.Expires < DateTimeOffset.UtcNow)
                {
                    throw new OperationInstallException("LOGIN_CHALLENGE_EXPIRED");
                }

                using (var auth = new ExternalAuthentication())
                {
                    var login = await auth.SelectProfileAsync(
                        challengeRow.Challenge,
                        Text("profileId"),
                        ct
                    );
                    LauncherAccount saved;
                    try
                    {
                        saved = accounts.SaveExternal(login, (bool? )args["remember"] ?? false);
                    }
                    catch (CryptographicException)
                    {
                        saved = accounts.SaveExternalSession(login);
                    }

                    externalChallenges.Remove(
                        Text("handle")
                    );
                    return saved;
                }

            case "workspace.account.externalSkin":
            case "workspace.account.externalTexture":
                if (Account()
                    .Kind != AccountKind.External)
                {
                    throw new OperationInstallException("EXTERNAL_ACCOUNT_REQUIRED");
                }

                using (var auth = new ExternalAuthentication())
                {
                    var login = await accounts.ResolveExternalAsync(accountId, auth, ct);
                    if (method == "workspace.account.externalTexture")
                    {
                        var kind = Text("kind");
                        if (kind is not ("skin" or "cape"))
                        {
                            throw new OperationInstallException("ACCOUNT_ACTION_INVALID");
                        }

                        var bytes = Text("path")
                            .Length == 0
                            ? null
                            : await ReadPng(kind);
                        await auth.ChangeTextureAsync(login, kind, bytes, (bool? )args["slim"] ?? false, ct);
                        return true;
                    }

                    var textures = await auth.TexturesAsync(login.Server, login.Credential.Session.Uuid, ct);
                    return new View.ExternalSkin(
                        login.Server.Name,
                        login.Server.ApiRoot,
                        textures.Uploadable.ToArray(),
                        textures.SkinUrl is null
                            ? null
                            : SkinPixels.Modern(
                                await auth.DownloadTextureAsync(textures.SkinUrl, "skin", ct)
                            )
                    );
                }

            default:
                throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }
    }
}
