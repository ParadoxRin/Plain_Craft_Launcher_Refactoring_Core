namespace Pcl.Core.Contracts.Accounts;
// Shell displays the challenge and submits cancel intent; authentication and credential persistence remain behind this interface.
public interface IAccountLogin
{
    Task<string> LoginAsync(bool remember, Action<LoginPrompt> prompt, CancellationToken ct);
}
