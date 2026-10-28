using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Credentials;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Host;
using Pcl.Client;

// 只用随机合成值；子进程输入走 stdin，不把秘密传入命令行、报告或公共 RPC。
if (args.Length == 1 && args[0] is "read-child" or "unavailable-child" or "locked-child")
{
    var data = JsonSerializer.Deserialize<Probe>(await Console.In.ReadToEndAsync())!;
    var adapter = PlatformAccountSecrets.Create(data.Scope);
    try
    {
        if (args[0] == "read-child")
        {
            if (adapter.Get(data.Id) != data.Secret)
            {
                throw new InvalidOperationException("CROSS_PROCESS_MISMATCH");
            }
        }
        else
        {
            foreach (var action in new Action[] { () => adapter.Get(data.Id), () => adapter.Set(data.Id, "not-written"), () => adapter.Delete(data.Id) })
            {
                bool rejected = false;
                try
                {
                    action();
                }
                catch (CryptographicException)
                {
                    rejected = true;
                }
                if (!rejected)
                {
                    throw new InvalidOperationException("EXPECTED_STORE_REJECTION");
                }
            }
        }
        Console.WriteLine("PROBE_OK");
    }
    catch
    {
        Console.Error.WriteLine("PROBE_FAILED");
        Environment.ExitCode = 1;
    }
    return;
}
if (args.Length != 3)
{
    throw new ArgumentException("<fresh physical data root> <report.json> <Host.dll>");
}
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
{
    throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
}
Directory.CreateDirectory(root);
var report = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var checks = new List<string>();
var errors = new List<string>();
var stores = new List<(IAccountSecrets Store, string Id)>();
HostClient? host = null;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var ct = timeout.Token;
var scope = Path.Combine(root, "first");
var otherScope = Path.Combine(root, "second");
var id = "synthetic-" + Guid.NewGuid().ToString("N");
var secret = "合成凭据-" + Guid.NewGuid().ToString("N") + "-😀";
var secrets = PlatformAccountSecrets.Create(scope);
void Check(bool ok, string name)
{
    if (!ok)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task ProbeChild(string mode, Probe input, bool unavailable = false)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add(typeof(Probe).Assembly.Location);
    start.ArgumentList.Add(mode);
    if (unavailable)
    {
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(root, "missing-session-bus");
        start.Environment["DISPLAY"] = "";
    }
    using var child = Process.Start(start)!;
    var output = child.StandardOutput.ReadToEndAsync(ct);
    var error = child.StandardError.ReadToEndAsync(ct);
    await child.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
    child.StandardInput.Close();
    try
    {
        await child.WaitForExitAsync(ct);
        Check(child.ExitCode == 0 && (await output).Trim() == "PROBE_OK" && string.IsNullOrWhiteSpace(await error), mode);
    }
    finally
    {
        if (!child.HasExited)
        {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }
}
// 仅供上面显式隔离的 Linux 原生服务测试；输出不进入报告。
async Task KeyringCommand(string executable, string[] arguments, string? input = null)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync(ct);
    var stderr = process.StandardError.ReadToEndAsync(ct);
    if (input is not null)
    {
        await process.StandardInput.WriteAsync(input);
    }
    process.StandardInput.Close();
    try
    {
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ISOLATED_KEYRING_CONTROL_FAILED");
        }
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
    }
}
try
{
    Check(OperatingSystem.IsMacOS() ? secrets is KeychainAccountSecrets
        : OperatingSystem.IsWindows() ? secrets is WindowsCredentialAccountSecrets
        : OperatingSystem.IsLinux() && secrets is LinuxSecretServiceAccountSecrets, "native-provider-selected-from-host-os");
    stores.Add((secrets, id));
    Check(secrets.Get(id) is null, "missing-credential-is-null");
    secrets.Delete(id);
    Check(true, "missing-delete-is-idempotent");
    secrets.Set(id, secret);
    Check(PlatformAccountSecrets.Create(scope).Get(id) == secret, "unicode-roundtrip-new-adapter");
    await ProbeChild("read-child", new(scope, id, secret));
    var other = PlatformAccountSecrets.Create(otherScope);
    stores.Add((other, id));
    Check(other.Get(id) is null, "different-root-cannot-read-credential");
    other.Set(id, "other-value");
    secrets.Set(id, secret + "updated");
    Check(secrets.Get(id) == secret + "updated" && other.Get(id) == "other-value", "overwrite-preserves-other-root");
    var secondId = id + "-second";
    stores.Add((secrets, secondId));
    secrets.Set(secondId, "second-account");
    secrets.Delete(id);
    Check(secrets.Get(id) is null && secrets.Get(secondId) == "second-account", "delete-preserves-other-account");
    if (OperatingSystem.IsMacOS())
    {
        var legacy = new MacAccountSecrets(scope);
        Check(legacy.Get(secondId) == "second-account", "mac-historical-service-identity-readable");
        legacy.Set(secondId, "legacy-update");
        Check(secrets.Get(secondId) == "legacy-update", "mac-existing-adapter-write-compatible");
    }
    else
    {
        var maximum = OperatingSystem.IsWindows() ? 2560 : 65536;
        var boundary = new string('a', maximum - 3) + "界";
        secrets.Set(id, boundary);
        Check(secrets.Get(id) == boundary, "native-byte-limit-roundtrip");
        bool rejected = false;
        try
        {
            secrets.Set(id, boundary + "!");
        }
        catch (CryptographicException)
        {
            rejected = true;
        }
        Check(rejected && secrets.Get(id) == boundary, "oversized-write-preserves-old-secret");
        foreach (var bad in new[] { "", "bad\0value" })
        {
            rejected = false;
            try
            {
                secrets.Set(id, bad);
            }
            catch (CryptographicException)
            {
                rejected = true;
            }
            Check(rejected && secrets.Get(id) == boundary, bad.Length == 0 ? "empty-secret-rejected" : "nul-secret-rejected");
        }
        secrets.Set(id, secret);
    }
    if (OperatingSystem.IsLinux())
    {
        // 本项会锁定整个测试 collection，只允许显式准备的隔离 D-Bus/临时 keyring。
        Check(Environment.GetEnvironmentVariable("PCL_TEST_PRIVATE_SECRET_SERVICE") == "1", "linux-private-service-required");
        await ProbeChild("unavailable-child", new(scope, id, secret), true);
        Check(secrets.Get(id) == secret, "unavailable-session-does-not-delete-existing-secret");
        await KeyringCommand("gdbus", ["call", "--session", "--dest", "org.freedesktop.secrets",
            "--object-path", "/org/freedesktop/secrets", "--method", "org.freedesktop.Secret.Service.Lock",
            "[objectpath '/org/freedesktop/secrets/collection/login']"]);
        try
        {
            await ProbeChild("locked-child", new(scope, id, secret));
        }
        finally
        {
            await KeyringCommand("gnome-keyring-daemon", ["--unlock", "--components=secrets"], "pcl-ci-fixture-only\n");
        }
        Check(secrets.Get(id) == secret, "locked-store-rejection-preserves-secret-after-unlock");
    }

    // 合成账户只用于持久化/退出，不发送到微软，也不调用游戏启动或权益接口。
    var paths = new AppPaths(Path.Combine(root, "host-data"));
    var accountSecrets = PlatformAccountSecrets.Create(paths.Root);
    var manager = new AccountManager(paths, accountSecrets);
    var uuid = Guid.NewGuid().ToString("N");
    var login = new MicrosoftLogin(new("FixturePlayer", uuid, "synthetic-access", "msa", false),
        secret, DateTimeOffset.UtcNow.AddHours(1), "00000000-0000-0000-0000-000000000001");
    var account = manager.SaveMicrosoft(login, true);
    stores.Add((accountSecrets, account.Id));
    Check(account.Remembered && accountSecrets.Get(account.Id) == secret, "account-manager-remembers-only-through-native-store");
    var indexText = File.ReadAllText(Path.Combine(paths.Root, "accounts.json"));
    Check(!indexText.Contains(secret) && !indexText.Contains("synthetic-access"), "public-account-index-has-no-tokens");
    host = new HostClient(Environment.ProcessPath!, Path.GetFullPath(args[2]), paths.Root, reconnectable: true);
    await host.Call<HostHello>("host.hello", new { minimumVersion = 1, maximumVersion = 1 }, ct);
    var snapshot = await host.Call<AccountSnapshot>("account.snapshot", new { }, ct);
    Check(snapshot.Accounts is [{ Remembered: true, HasCredential: true }], "real-host-restores-public-account-metadata");
    Check(!JsonSerializer.Serialize(snapshot).Contains(secret), "host-snapshot-has-no-secret");
    await host.Call<JsonElement>("account.logout", new { accountId = account.Id }, ct);
    snapshot = await host.Call<AccountSnapshot>("account.snapshot", new { }, ct);
    Check(snapshot.Accounts is [{ Remembered: false, HasCredential: false }] && accountSecrets.Get(account.Id) is null,
        "real-host-logout-deletes-native-credential");
    await host.ShutdownAsync(ct);
    await host.DisconnectAsync();
    manager = new AccountManager(paths, accountSecrets);
    account = manager.SaveMicrosoft(login, true);
    await host.Call<HostHello>("host.hello", new { minimumVersion = 1, maximumVersion = 1 }, ct);
    await host.Call<JsonElement>("account.remove", new { accountId = account.Id }, ct);
    snapshot = await host.Call<AccountSnapshot>("account.snapshot", new { }, ct);
    Check(snapshot.Accounts.Length == 0 && accountSecrets.Get(account.Id) is null, "real-host-restart-remove-clears-native-store-and-index");
    await host.ShutdownAsync(ct);
    Check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(path =>
        File.ReadAllText(path).Contains(secret)), "data-root-never-contains-synthetic-secret");
}
catch (Exception ex)
{
    // 不输出异常 message/stack，避免原生服务或误断言回显合成秘密/本机路径。
    errors.Add(ex.GetType().Name);
    Console.Error.WriteLine("FAIL " + ex.GetType().Name);
    Environment.ExitCode = 1;
}
finally
{
    if (host is not null)
    {
        try
        {
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.ShutdownAsync(close.Token);
        }
        catch (Exception ex)
        {
            errors.Add("host-cleanup-" + ex.GetType().Name);
            Environment.ExitCode = 1;
        }
        await host.DisposeAsync();
    }
    foreach (var pair in stores)
    {
        try
        {
            pair.Store.Delete(pair.Id);
            if (pair.Store.Get(pair.Id) is not null)
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex)
        {
            errors.Add("credential-cleanup-" + ex.GetType().Name);
            Environment.ExitCode = 1;
        }
    }
    if (errors.Count == 0)
    {
        Check(true, "all-owned-synthetic-credentials-removed");
    }
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        status = errors.Count == 0 ? "passed" : "failed",
        platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        provider = secrets.GetType().Name,
        scope = "Real native credential CRUD and cross-process recovery, real Host logout/remove using synthetic account data. No Microsoft authentication or game entitlement claim.",
        checks, errors
    }, new JsonSerializerOptions { WriteIndented = true }));
}
record Probe(string Scope, string Id, string Secret);
