using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Pcl.Core.Adapters.Credentials;

// 直接调用 libsecret；需要当前用户会话的 Secret Service，不启动 shell，不写明文降级文件。
public sealed class LinuxSecretServiceAccountSecrets(string scope) : IAccountSecrets
{
    private readonly string scopeKey = CredentialIdentity.Scope(scope);
    private const string SecretLibrary = "libsecret-1.so.0", Glib = "libglib-2.0.so.0";
    private const string ObjectLibrary = "libgobject-2.0.so.0", Gio = "libgio-2.0.so.0";
    private const int MaximumBytes = 65536;
    private static readonly Lazy<nint> GlibHandle = new(() => NativeLibrary.Load(Glib));

    public string? Get(string id) => Access(id, call =>
    {
        using var items = Search(call, true);
        if (items.Values.Count == 0)
        {
            return null;
        }
        // 一个作用域/账户只允许一个值；异常重复项不能随意取第一个，避免恢复错误身份。
        if (items.Values.Count != 1)
        {
            throw Failure();
        }
        var value = secret_item_get_secret(items.Values[0]);
        if (value == 0)
        {
            throw Failure();
        }
        try
        {
            var pointer = secret_value_get(value, out var length);
            if (pointer == 0 || length > MaximumBytes)
            {
                throw Failure();
            }
            var bytes = new byte[(int)length];
            try
            {
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return CredentialIdentity.Utf8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            secret_value_unref(value);
        }
    });

    public void Set(string id, string secret)
    {
        var bytes = CredentialIdentity.Secret(secret, MaximumBytes);
        try
        {
            Access(id, call =>
            {
                // 先确认匹配项均已解锁；拒绝把锁住的旧值当作不存在并另建重复项。
                using var items = Search(call, false);
                var value = secret_value_new(bytes, bytes.Length, "text/plain; charset=utf-8");
                if (value == 0)
                {
                    throw Failure();
                }
                try
                {
                    var ok = secret_service_store_sync(call.Service, 0, call.Attributes, null,
                        "Plain Craft Launcher Refactoring account", value, call.Cancellable, out var error);
                    Check(error);
                    if (ok == 0)
                    {
                        throw Failure();
                    }
                }
                finally
                {
                    secret_value_unref(value);
                }
                return true;
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete(string id) => Access(id, call =>
    {
        // password_clearv 会跳过锁住的值；必须先搜索/解锁并逐项确认删除，不能假报退出成功。
        using var items = Search(call, false);
        foreach (var item in items.Values)
        {
            var ok = secret_item_delete_sync(item, call.Cancellable, out var error);
            Check(error);
            if (ok == 0)
            {
                throw Failure();
            }
        }
        return true;
    });

    private T Access<T>(string id, Func<NativeCall, T> action)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("此凭据适配器需要 Linux Secret Service。");
        }
        var account = CredentialIdentity.Account(id);
        try
        {
            using var call = new NativeCall(scopeKey, account);
            return action(call);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 不泄漏原生异常路径；上层沿用 CryptographicException 的仅本次会话处理。
            throw new CryptographicException("Linux 系统凭据存储不可用，请安装 libsecret 并启用用户 Secret Service；不会写入明文文件。");
        }
    }

    private static Items Search(NativeCall call, bool load)
    {
        // ALL=2、UNLOCK=4、LOAD_SECRETS=8；解锁失败时 search 仍可能成功，须额外核对 locked。
        var list = secret_service_search_sync(call.Service, 0, call.Attributes, 2 | 4 | (load ? 8 : 0), call.Cancellable, out var error);
        var items = new Items(list);
        try
        {
            Check(error);
            if (items.Values.Any(item => secret_item_get_locked(item) != 0))
            {
                throw new CryptographicException("Linux 凭据存储仍被锁定或解锁被拒绝；原有凭据未删除。");
            }
            return items;
        }
        catch
        {
            items.Dispose();
            throw;
        }
    }
    private static CryptographicException Failure() => new("Linux 凭据存储操作未完成；请检查用户 Secret Service 或解锁提示。");
    private static void Check(nint error)
    {
        if (error == 0)
        {
            return;
        }
        // GError 的 message 不进入日志，服务可能在错误中回显属性；统一为不含秘密的错误。
        g_error_free(error);
        throw Failure();
    }

    // 同步 Port 仍由 Host 后台任务调用。GCancellable 给系统提示/服务调用30秒预算；回调结束后才释放对象。
    private sealed class NativeCall : IDisposable
    {
        internal nint Attributes { get; private set; }
        internal nint Cancellable { get; private set; }
        internal nint Service { get; private set; }
        private readonly List<nint> strings = new();
        private readonly CancellationTokenSource timeout = new();
        private CancellationTokenRegistration registration;
        internal NativeCall(string scope, string account)
        {
            try
            {
                Cancellable = g_cancellable_new();
                registration = timeout.Token.Register(() => g_cancellable_cancel(Cancellable));
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                // 每次调用持有独立 proxy，避免默认缓存仍绑定已退出的 Secret Service 唯一名称。
                // 服务重启后下一次操作重新打开；写入失败不在内部盲重试。
                Service = secret_service_open_sync(secret_service_get_type(), null, 0, Cancellable, out var error);
                Check(error);
                if (Service == 0)
                {
                    throw Failure();
                }
                Attributes = g_hash_table_new(NativeLibrary.GetExport(GlibHandle.Value, "g_str_hash"),
                    NativeLibrary.GetExport(GlibHandle.Value, "g_str_equal"));
                Add("application", "plain-craft-launcher-refactoring");
                Add("scope-v1", scope);
                Add("account-v1", account);
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        private void Add(string key, string value)
        {
            var nativeKey = Marshal.StringToCoTaskMemUTF8(key);
            strings.Add(nativeKey);
            var nativeValue = Marshal.StringToCoTaskMemUTF8(value);
            strings.Add(nativeValue);
            g_hash_table_insert(Attributes, nativeKey, nativeValue);
        }
        public void Dispose()
        {
            registration.Dispose();
            timeout.Dispose();
            if (Attributes != 0)
            {
                g_hash_table_unref(Attributes);
                Attributes = 0;
            }
            foreach (var pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
            strings.Clear();
            if (Service != 0)
            {
                g_object_unref(Service);
                Service = 0;
            }
            if (Cancellable != 0)
            {
                g_object_unref(Cancellable);
                Cancellable = 0;
            }
        }
    }
    private sealed class Items(nint list) : IDisposable
    {
        internal List<nint> Values { get; } = Read(list);
        private static List<nint> Read(nint list)
        {
            var values = new List<nint>();
            for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                values.Add(Marshal.ReadIntPtr(node));
            }
            return values;
        }
        public void Dispose()
        {
            foreach (var value in Values)
            {
                g_object_unref(value);
            }
            g_list_free(list);
        }
    }

    [DllImport(SecretLibrary)] private static extern nint secret_service_search_sync(nint service, nint schema, nint attributes, int flags, nint cancellable, out nint error);
    [DllImport(SecretLibrary)] private static extern int secret_item_get_locked(nint item);
    [DllImport(SecretLibrary)] private static extern nint secret_item_get_secret(nint item);
    [DllImport(SecretLibrary)] private static extern nint secret_value_get(nint value, out nuint length);
    [DllImport(SecretLibrary)] private static extern void secret_value_unref(nint value);
    [DllImport(SecretLibrary)] private static extern int secret_item_delete_sync(nint item, nint cancellable, out nint error);
    [DllImport(SecretLibrary)] private static extern nuint secret_service_get_type();
    [DllImport(SecretLibrary)] private static extern nint secret_service_open_sync(nuint type,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? busName, int flags, nint cancellable, out nint error);
    [DllImport(SecretLibrary)] private static extern nint secret_value_new(byte[] value, nint length,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);
    [DllImport(SecretLibrary)] private static extern int secret_service_store_sync(nint service, nint schema, nint attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? collection, [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        nint value, nint cancellable, out nint error);
    [DllImport(Glib)] private static extern nint g_hash_table_new(nint hash, nint equal);
    [DllImport(Glib)] private static extern int g_hash_table_insert(nint table, nint key, nint value);
    [DllImport(Glib)] private static extern void g_hash_table_unref(nint table);
    [DllImport(Glib)] private static extern void g_list_free(nint list);
    [DllImport(Glib)] private static extern void g_error_free(nint error);
    [DllImport(ObjectLibrary)] private static extern void g_object_unref(nint value);
    [DllImport(Gio)] private static extern nint g_cancellable_new();
    [DllImport(Gio)] private static extern void g_cancellable_cancel(nint cancellable);
}
