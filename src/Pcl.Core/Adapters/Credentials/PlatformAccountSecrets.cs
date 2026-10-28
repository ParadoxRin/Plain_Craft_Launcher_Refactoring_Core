using System.Security.Cryptography;
using System.Text;

namespace Pcl.Core.Adapters.Credentials;

// Host 和旧执行入口共同使用的宿主凭据选择；按实际 OS，不按 Minecraft 目标平台选择。
public static class PlatformAccountSecrets
{
    public static IAccountSecrets Create(string scope)
    {
        if (OperatingSystem.IsMacOS())
        {
            // 保留历史 service 名和路径哈希，升级不会使现有 Mac 登录凭据失联。
            return new Mac.KeychainAccountSecrets(scope);
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialAccountSecrets(scope);
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceAccountSecrets(scope);
        }
        throw new PlatformNotSupportedException("当前宿主没有受支持的系统凭据存储。");
    }
}

// 新平台只将作用域/账户的哈希放进可见原生索引；不包含目录、邮箱或令牌。
internal static class CredentialIdentity
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Scope(string scope) => Hash(Path.GetFullPath(scope));
    internal static string Account(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl))
        {
            throw new ArgumentException("账户标识无效。");
        }
        return Hash(id);
    }
    internal static byte[] Secret(string secret, int maximum)
    {
        if (string.IsNullOrEmpty(secret) || secret.Contains('\0') || Utf8.GetByteCount(secret) > maximum)
        {
            throw new CryptographicException("凭据超出系统存储限制，原有凭据未修改。");
        }
        return Utf8.GetBytes(secret);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))).ToLowerInvariant();
}
