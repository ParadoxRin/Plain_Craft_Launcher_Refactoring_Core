using Pcl.Core.Contracts.Accounts;
using System.Security.Cryptography;

namespace Pcl.Core.Adapters.Accounts;
// Explicit in-process compatibility mode only. The production desktop receives the Client implementation from Bootstrap.
public sealed class LocalAccountLogin(AccountManager accounts) : IAccountLogin
{
    public async Task<string> LoginAsync(bool remember, Action<LoginPrompt> prompt, CancellationToken ct)
    {
        using var auth = new MicrosoftAuthentication();
        var clientId = MicrosoftApplication.BuiltIn.RequireClientId();
        var challenge = await auth.BeginAsync(clientId, ct);
        prompt(
            new(challenge.UserCode, challenge.VerificationUri, challenge.ExpiresIn)
        );
        var login = await auth.CompleteLoginAsync(clientId, challenge, ct);
        try
        {
            return accounts.SaveMicrosoft(login, remember)
                .Id;
        }
        catch (CryptographicException)
        {
            return accounts.SaveSession(login)
                .Id;
        }
    }
}
