namespace Pcl.Core;
/// <summary>旧调用方兼容门面；原生实现已移入 Adapter，service 哈希、账户键及存储语义保持不变。</summary>
public sealed class MacAccountSecrets(string scope) : IAccountSecrets
{
    private readonly IAccountSecrets adapter = new Adapters.Mac.KeychainAccountSecrets(scope);
    public string? Get(string id) => adapter.Get(id);
    public void Set(string id, string secret) => adapter.Set(id, secret);
    public void Delete(string id) => adapter.Delete(id);
}
