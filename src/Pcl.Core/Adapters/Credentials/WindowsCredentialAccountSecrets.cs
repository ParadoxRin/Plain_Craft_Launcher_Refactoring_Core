using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Pcl.Core.Adapters.Credentials;

// Windows Generic Credential，当前用户/本机持久化。秘密直接传给 Advapi32，不经过 argv 或配置文件。
public sealed class WindowsCredentialAccountSecrets(string scope) : IAccountSecrets
{
    private readonly string prefix = "local.pcl.refactoring.oauth.v1/" + CredentialIdentity.Scope(scope) + "/";
    private const uint Generic = 1, LocalMachinePersistence = 2;
    private const int NotFound = 1168, MaximumBytes = 2560;

    public string? Get(string id)
    {
        var target = Target(id);
        if (!CredReadW(target, Generic, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NotFound)
            {
                return null;
            }
            throw Failure(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > MaximumBytes || credential.Blob == 0)
            {
                throw Failure(0);
            }
            var bytes = new byte[credential.BlobSize];
            try
            {
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                return CredentialIdentity.Utf8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                // 返回的原生 blob 由 CredFree 释放；先清除本进程中的明文副本。
                Marshal.Copy(new byte[credential.BlobSize], 0, credential.Blob, (int)credential.BlobSize);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string id, string secret)
    {
        var target = Target(id);
        // 原生上限以字节计算，超限必须在写入之前拒绝，不能截断刷新令牌。
        var bytes = CredentialIdentity.Secret(secret, MaximumBytes);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = Generic,
                TargetName = target,
                BlobSize = (uint)bytes.Length,
                Blob = blob,
                Persist = LocalMachinePersistence,
                UserName = "PCL"
            };
            if (!CredWriteW(ref credential, 0))
            {
                throw Failure(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string id)
    {
        if (!CredDeleteW(Target(id), Generic, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NotFound)
            {
                throw Failure(error);
            }
        }
    }

    private string Target(string id)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("此凭据适配器需要 Windows。");
        }
        return prefix + CredentialIdentity.Account(id);
    }
    private static CryptographicException Failure(int code) => new($"Windows 凭据存储访问失败（{code}）；请检查当前用户登录会话。");

    // 按 Win32 CREDENTIALW 自然对齐；FILETIME 为两个 DWORD，支持 x64/ARM64。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);
}
