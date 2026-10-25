using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pcl.Core.Adapters.Mac;
// Values are passed directly to Security.framework; no token appears in argv, shell text or files.
public sealed class KeychainAccountSecrets(string scope) : IAccountSecrets
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> Library = new(
        () => NativeLibrary.Load(Security)
    );
    private readonly string service = "local.pcl.macport.oauth." + Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                Path.GetFullPath(scope)
            )
        )
    )[..20].ToLowerInvariant();
    // [实现] Key 的实现：计算并返回 Marshal.ReadIntPtr( NativeLibrary.GetExport(Library.Value, name) )。
    private static nint Key(string name) => Marshal.ReadIntPtr(
        NativeLibrary.GetExport(Library.Value, name)
    );
    // [实现] Get 的实现：准备 var query = Query(id)；执行 query.Set( "kSecReturnData", query.BooleanTrue() )；执行 query.Set( "kSecMatchLimit", Key("kSecMatchLimitOne") )。
    public string? Get(string id)
    {
        using var query = Query(id);
        query.Set(
            "kSecReturnData",
            query.BooleanTrue()
        );
        query.Set(
            "kSecMatchLimit",
            Key("kSecMatchLimitOne")
        );
        var status = SecItemCopyMatching(query.Handle, out var result);
        if (status == -25300)
            return null;
        Check(status);
        try
        {
            // [实现] 在异常保护范围内：准备 var length = CFDataGetLength(result)；按 length > 65536 分支处理；准备 var bytes = new byte[(int)length]。
            var length = CFDataGetLength(result);
            if (length > 65536)
                throw new CryptographicException("账户凭据长度无效。");
            var bytes = new byte[(int)length];
            Marshal.Copy(
                CFDataGetBytePtr(result),
                bytes,
                0,
                bytes.Length
            );
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 result != 0 分支处理。
            if (result != 0)
                CFRelease(result);
        }
    }

    // [实现] Set 的实现：准备 var query = Query(id)；准备 var values = new NativeQuery()；执行 values.Data( "kSecValueData", Encoding.UTF8.GetBytes(secret) )。
    public void Set(string id, string secret)
    {
        using var query = Query(id);
        using var values = new NativeQuery();
        values.Data(
            "kSecValueData",
            Encoding.UTF8.GetBytes(secret)
        );
        var status = SecItemUpdate(query.Handle, values.Handle);
        if (status == -25300)
        {
            // [实现] 条件成立（status == -25300）时：执行 query.Data( "kSecValueData", Encoding.UTF8.GetBytes(secret) )；执行 status = SecItemAdd(query.Handle, 0)。
            query.Data(
                "kSecValueData",
                Encoding.UTF8.GetBytes(secret)
            );
            status = SecItemAdd(query.Handle, 0);
        }

        Check(status);
    }

    // [实现] Delete 的实现：准备 var query = Query(id)；准备 var status = SecItemDelete(query.Handle)；按 status != -25300 分支处理。
    public void Delete(string id)
    {
        using var query = Query(id);
        var status = SecItemDelete(query.Handle);
        if (status != -25300)
            Check(status);
    }

    // [实现] Query 的实现：按 !OperatingSystem.IsMacOS() 分支处理；按 string.IsNullOrWhiteSpace(id) || id.Length > 200 分支处理；准备 var query = new NativeQuery()。
    private NativeQuery Query(string id)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("持久账户登录当前使用 macOS 钥匙串。");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            throw new ArgumentException("账户标识无效。");
        var query = new NativeQuery();
        query.Set(
            "kSecClass",
            Key("kSecClassGenericPassword")
        );
        query.Text("kSecAttrService", service);
        query.Text("kSecAttrAccount", id);
        return query;
    }

    // [实现] Check 的实现：按 status != 0 分支处理。
    private static void Check(int status)
    {
        if (status != 0)
            throw new CryptographicException($"系统钥匙串访问失败（{status}）。可关闭“记住登录”后重试。");
    }

    // 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。
    private sealed class NativeQuery : IDisposable
    {
        public nint Handle { get; } = CFDictionaryCreateMutable(0, 0, 0, 0);

        private readonly List<nint> allocated = new();
        // [实现] Set 的实现：执行 CFDictionarySetValue( Handle, Key(key), value )。
        public void Set(string key, nint value) => CFDictionarySetValue(
            Handle,
            Key(key),
            value
        );
        // [实现] Text 的实现：准备 var value = CFStringCreateWithCString(0, text, 0x08000100)；执行 allocated.Add(value)；执行 Set(key, value)。
        public void Text(string key, string text)
        {
            var value = CFStringCreateWithCString(0, text, 0x08000100);
            allocated.Add(value);
            Set(key, value);
        }

        // [实现] Data 的实现：准备 var value = CFDataCreate(0, bytes, bytes.Length)；执行 allocated.Add(value)；执行 Set(key, value)。
        public void Data(string key, byte[] bytes)
        {
            var value = CFDataCreate(0, bytes, bytes.Length);
            allocated.Add(value);
            Set(key, value);
        }

        // [实现] BooleanTrue 的实现：返回 Marshal.ReadIntPtr( NativeLibrary.GetExport(CFLibrary.Value, "kCFBooleanTrue") )。
        public nint BooleanTrue()
        {
            return Marshal.ReadIntPtr(
                NativeLibrary.GetExport(CFLibrary.Value, "kCFBooleanTrue")
            );
        }

        // [实现] Dispose 的实现：执行 CFRelease(Handle)；遍历 allocated 处理 value。
        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var value in allocated)
                CFRelease(value);
        }
    }

    private static readonly Lazy<nint> CFLibrary = new(
        () => NativeLibrary.Load(CF)
    );
    // [实现] SecItemCopyMatching 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(Security)]
    private static extern int SecItemCopyMatching(nint query, out nint result);
    // [实现] SecItemAdd 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(Security)]
    private static extern int SecItemAdd(nint query, nint result);
    // [实现] SecItemUpdate 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(Security)]
    private static extern int SecItemUpdate(nint query, nint attributes);
    // [实现] SecItemDelete 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(Security)]
    private static extern int SecItemDelete(nint query);
    // [实现] CFDictionaryCreateMutable 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    // [实现] CFDictionarySetValue 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    // [实现] CFStringCreateWithCString 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    // [实现] CFDataCreate 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    // [实现] CFDataGetLength 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern nint CFDataGetLength(nint data);
    // [实现] CFDataGetBytePtr 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern nint CFDataGetBytePtr(nint data);
    // [实现] CFRelease 的实现：仅声明调用契约，由实现类提供行为。
    [DllImport(CF)]
    private static extern void CFRelease(nint value);
}
